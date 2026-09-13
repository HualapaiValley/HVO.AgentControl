using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<ParticipantEnrollment> CreateEnrollment(CreateEnrollmentInput input) => Write(async db =>
    {
        if (string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 200)
            throw new ControlException("Enrollment ID is required and must be 200 characters or fewer.", 400);
        if (string.IsNullOrWhiteSpace(input.AdapterType) || input.AdapterType.Length > 100)
            throw new ControlException("Adapter type is required and must be 100 characters or fewer.", 400);
        if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 500)
            throw new ControlException("Display name is required and must be 500 characters or fewer.", 400);
        var existing = await db.Enrollments.FindAsync(input.Id);
        if (existing is not null) throw new ControlException("Enrollment ID already exists.", 409);
        var enrollment = new ParticipantEnrollment
        {
            Id = input.Id,
            AdapterType = input.AdapterType,
            DisplayName = input.DisplayName,
            State = EnrollmentState.Active,
            AuthorityGeneration = 1,
            EnrolledAt = ControlStore.Now,
            LastSeenAt = ControlStore.Now
        };
        db.Enrollments.Add(enrollment);
        Event(db, "EnrollmentCreated", payload: new { enrollment.Id, enrollment.AdapterType, enrollment.DisplayName }, provenance: "user");
        return enrollment;
    });

    public Task<ParticipantEnrollment> GetEnrollment(string id) => Read(async db => await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw new ControlException("Enrollment not found.", 404));

    public Task<List<ParticipantEnrollment>> GetActiveEnrollments() => Read(db => db.Enrollments.AsNoTracking().Where(x => x.State == EnrollmentState.Active).ToListAsync());

    public Task<ParticipantEnrollment> SuspendEnrollment(string id) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(id) ?? throw new ControlException("Enrollment not found.", 404);
        if (enrollment.State == EnrollmentState.Revoked) throw new ControlException("Cannot suspend a revoked enrollment.");
        enrollment.State = EnrollmentState.Suspended;
        enrollment.Revision++;
        Event(db, "EnrollmentSuspended", payload: new { enrollment.Id }, provenance: "user");
        return enrollment;
    });

    public Task<ParticipantEnrollment> RevokeEnrollment(string id) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(id) ?? throw new ControlException("Enrollment not found.", 404);
        enrollment.State = EnrollmentState.Revoked;
        enrollment.Revision++;
        Event(db, "EnrollmentRevoked", payload: new { enrollment.Id }, provenance: "user");
        return enrollment;
    });

    public Task<ParticipantEnrollment> ReactivateEnrollment(string id) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(id) ?? throw new ControlException("Enrollment not found.", 404);
        if (enrollment.State == EnrollmentState.Revoked) throw new ControlException("Cannot reactivate a revoked enrollment.");
        enrollment.State = EnrollmentState.Active;
        enrollment.LastSeenAt = ControlStore.Now;
        enrollment.Revision++;
        Event(db, "EnrollmentReactivated", payload: new { enrollment.Id }, provenance: "user");
        return enrollment;
    });

    public Task<ParticipantEnrollment> TouchEnrollment(string id) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(id) ?? throw new ControlException("Enrollment not found.", 404);
        if (enrollment.State != EnrollmentState.Active) throw new ControlException("Cannot touch a non-active enrollment.");
        enrollment.LastSeenAt = ControlStore.Now;
        return enrollment;
    });

    public Task<ParticipantEnrollment> AdvanceAuthority(AdvanceAuthorityInput input) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(input.EnrollmentId) ?? throw new ControlException("Enrollment not found.", 404);
        if (enrollment.State != EnrollmentState.Active) throw new ControlException("Cannot advance authority for non-active enrollment.");
        if (enrollment.AuthorityGeneration != input.ExpectedGeneration) throw new ControlException("Authority generation mismatch; refresh and retry.", 409);
        enrollment.AuthorityGeneration++;
        enrollment.Revision++;
        Event(db, "AuthorityAdvanced", payload: new { enrollment.Id, enrollment.AuthorityGeneration }, provenance: "user");
        return enrollment;
    });

    public Task<CommandAuthority> BindCommandAuthority(BindCommandAuthorityInput input) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(input.EnrollmentId) ?? throw new ControlException("Enrollment not found.", 404);
        if (enrollment.State != EnrollmentState.Active) throw new ControlException("Cannot bind authority to non-active enrollment.");
        var existing = await db.CommandAuthorities.FirstOrDefaultAsync(x => x.CommandId == input.CommandId);
        if (existing is not null)
        {
            if (existing.EnrollmentId != input.EnrollmentId) throw new ControlException("Command already bound to a different enrollment.");
            if (existing.AuthorityGeneration != enrollment.AuthorityGeneration || existing.Attempt != input.Attempt)
            {
                if (existing.AcknowledgedAt is not null)
                {
                    Event(db, "CommandAuthoritySuperseded", payload: new
                    {
                        existing.CommandId,
                        existing.EnrollmentId,
                        SupersededGeneration = existing.AuthorityGeneration,
                        SupersededAttempt = existing.Attempt,
                        SupersededAt = existing.AcknowledgedAt,
                        SupersededData = existing.AcknowledgementData
                    }, provenance: "system");
                }
                existing.AuthorityGeneration = enrollment.AuthorityGeneration;
                existing.Attempt = input.Attempt;
                existing.AcknowledgedAt = null;
                existing.AcknowledgementData = null;
            }
            return existing;
        }
        var authority = new CommandAuthority
        {
            CommandId = input.CommandId,
            EnrollmentId = input.EnrollmentId,
            AuthorityGeneration = enrollment.AuthorityGeneration,
            Attempt = input.Attempt,
            CreatedAt = ControlStore.Now
        };
        db.CommandAuthorities.Add(authority);
        Event(db, "CommandAuthorityBound", payload: new { authority.CommandId, authority.EnrollmentId, authority.AuthorityGeneration, authority.Attempt }, provenance: "user");
        return authority;
    });

    public Task<CommandAuthority?> GetCommandAuthority(string commandId) => Read(db => db.CommandAuthorities.AsNoTracking().FirstOrDefaultAsync(x => x.CommandId == commandId));

    public Task<CommandAuthority> AcknowledgeCommand(AcknowledgeCommandInput input) => Write(async db =>
    {
        var enrollment = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.EnrollmentId);
        if (enrollment is null || enrollment.State != EnrollmentState.Active)
            throw new ControlException("Enrollment is not active.", 400);
        if (enrollment.AuthorityGeneration != input.AuthorityGeneration)
            throw new ControlException("Authority generation mismatch; enrollment has advanced.", 409);
        var authority = await db.CommandAuthorities.FirstOrDefaultAsync(x =>
            x.CommandId == input.CommandId &&
            x.EnrollmentId == input.EnrollmentId &&
            x.AuthorityGeneration == input.AuthorityGeneration &&
            x.Attempt == input.Attempt)
            ?? throw new ControlException("Command authority not found for the specified generation and attempt.", 404);
        authority.AcknowledgedAt = ControlStore.Now;
        authority.AcknowledgementData = input.AcknowledgementData;
        Event(db, "CommandAcknowledged", payload: new { authority.CommandId, authority.EnrollmentId, authority.AuthorityGeneration, authority.Attempt }, provenance: "user");
        return authority;
    });

    public Task<bool> ValidateCommandAuthority(ValidateCommandAuthorityInput input)
    {
        return Read(async db =>
        {
            var enrollment = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.EnrollmentId);
            if (enrollment is null || enrollment.State != EnrollmentState.Active || enrollment.AuthorityGeneration != input.AuthorityGeneration) return false;
            return await db.CommandAuthorities.AnyAsync(x =>
                x.CommandId == input.CommandId &&
                x.EnrollmentId == input.EnrollmentId &&
                x.AuthorityGeneration == input.AuthorityGeneration &&
                x.Attempt == input.Attempt);
        });
    }

    public Task<EvidenceCursor> AdvanceCursor(AdvanceCursorInput input) => Write(async db =>
    {
        var enrollment = await db.Enrollments.FindAsync(input.EnrollmentId) ?? throw new ControlException("Enrollment not found.", 404);
        var cursor = await db.EvidenceCursors.FirstOrDefaultAsync(x => x.EnrollmentId == input.EnrollmentId && x.CursorName == input.CursorName);
        if (cursor is null)
        {
            cursor = new EvidenceCursor
            {
                EnrollmentId = input.EnrollmentId,
                CursorName = input.CursorName,
                CursorValue = input.CursorValue,
                LastConsumedAt = ControlStore.Now
            };
            db.EvidenceCursors.Add(cursor);
        }
        else
        {
            if (string.Compare(input.CursorValue, cursor.CursorValue, StringComparison.Ordinal) <= 0)
                throw new ControlException("New cursor value must be greater than current value.");
            cursor.CursorValue = input.CursorValue;
            cursor.LastConsumedAt = ControlStore.Now;
        }
        Event(db, "CursorAdvanced", payload: new { cursor.EnrollmentId, cursor.CursorName, cursor.CursorValue }, provenance: "user");
        return cursor;
    });

    public Task<EvidenceCursor?> GetCursor(string enrollmentId, string cursorName) =>
        Read(db => db.EvidenceCursors.AsNoTracking().FirstOrDefaultAsync(x => x.EnrollmentId == enrollmentId && x.CursorName == cursorName));

    public Task<List<EvidenceCursor>> GetCursors(string enrollmentId) =>
        Read(db => db.EvidenceCursors.AsNoTracking().Where(x => x.EnrollmentId == enrollmentId).ToListAsync());

    public Task<bool> IsCommandAcknowledged(string commandId) =>
        Read(db => db.CommandAuthorities.AnyAsync(x => x.CommandId == commandId && x.AcknowledgedAt != null));
}
