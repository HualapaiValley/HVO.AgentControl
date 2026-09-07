using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class EnrollmentAuthorityTests
{
    [Fact]
    public async Task CreateEnrollmentSucceedsWithValidInput()
    {
        await using var app = new TestApp();
        var enrollment = await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-1", "OpenCode", "Test Adapter"));
        Assert.Equal("enroll-1", enrollment.Id);
        Assert.Equal("OpenCode", enrollment.AdapterType);
        Assert.Equal("Test Adapter", enrollment.DisplayName);
        Assert.Equal(EnrollmentState.Active, enrollment.State);
        Assert.Equal(1, enrollment.AuthorityGeneration);
        Assert.True(enrollment.EnrolledAt > 0);
    }

    [Fact]
    public async Task CreateEnrollmentWithDuplicateIdThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-dup", "OpenCode", "First"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-dup", "ClaudeCode", "Second")));
    }

    [Fact]
    public async Task CreateEnrollmentWithEmptyIdThrows()
    {
        await using var app = new TestApp();
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateEnrollment(new CreateEnrollmentInput("", "OpenCode", "Test")));
    }

    [Fact]
    public async Task GetEnrollmentReturnsEnrollment()
    {
        await using var app = new TestApp();
        var created = await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-get", "OpenCode", "Get Test"));
        var retrieved = await app.Store.GetEnrollment("enroll-get");
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(created.AuthorityGeneration, retrieved.AuthorityGeneration);
    }

    [Fact]
    public async Task GetActiveEnrollmentsReturnsOnlyActive()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-active", "OpenCode", "Active"));
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-suspend", "OpenCode", "Suspend"));
        await app.Store.SuspendEnrollment("enroll-suspend");
        var active = await app.Store.GetActiveEnrollments();
        Assert.Contains(active, x => x.Id == "enroll-active");
        Assert.DoesNotContain(active, x => x.Id == "enroll-suspend");
    }

    [Fact]
    public async Task SuspendEnrollmentSucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-suspend", "OpenCode", "Suspend"));
        var suspended = await app.Store.SuspendEnrollment("enroll-suspend");
        Assert.Equal(EnrollmentState.Suspended, suspended.State);
    }

    [Fact]
    public async Task SuspendRevokedEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-rev", "OpenCode", "Revoke"));
        await app.Store.RevokeEnrollment("enroll-rev");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SuspendEnrollment("enroll-rev"));
    }

    [Fact]
    public async Task RevokeEnrollmentSucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-revoke", "OpenCode", "Revoke"));
        var revoked = await app.Store.RevokeEnrollment("enroll-revoke");
        Assert.Equal(EnrollmentState.Revoked, revoked.State);
    }

    [Fact]
    public async Task ReactivateSuspendedEnrollmentSucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-react", "OpenCode", "React"));
        await app.Store.SuspendEnrollment("enroll-react");
        var reactivated = await app.Store.ReactivateEnrollment("enroll-react");
        Assert.Equal(EnrollmentState.Active, reactivated.State);
    }

    [Fact]
    public async Task ReactivateRevokedEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-rev2", "OpenCode", "Revoke2"));
        await app.Store.RevokeEnrollment("enroll-rev2");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReactivateEnrollment("enroll-rev2"));
    }

    [Fact]
    public async Task TouchEnrollmentUpdatesLastSeenAt()
    {
        await using var app = new TestApp();
        var enrollment = await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-touch", "OpenCode", "Touch"));
        var originalLastSeen = enrollment.LastSeenAt;
        await Task.Delay(10);
        var touched = await app.Store.TouchEnrollment("enroll-touch");
        Assert.True(touched.LastSeenAt >= originalLastSeen);
    }

    [Fact]
    public async Task TouchNonActiveEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-touch2", "OpenCode", "Touch2"));
        await app.Store.SuspendEnrollment("enroll-touch2");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.TouchEnrollment("enroll-touch2"));
    }

    [Fact]
    public async Task AdvanceAuthoritySucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-adv", "OpenCode", "Advance"));
        var advanced = await app.Store.AdvanceAuthority(new AdvanceAuthorityInput("enroll-adv", 1));
        Assert.Equal(2, advanced.AuthorityGeneration);
    }

    [Fact]
    public async Task AdvanceAuthorityWithWrongGenerationThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-adv2", "OpenCode", "Advance2"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvanceAuthority(new AdvanceAuthorityInput("enroll-adv2", 5)));
    }

    [Fact]
    public async Task AdvanceAuthorityForSuspendedEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-adv3", "OpenCode", "Advance3"));
        await app.Store.SuspendEnrollment("enroll-adv3");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvanceAuthority(new AdvanceAuthorityInput("enroll-adv3", 1)));
    }

    [Fact]
    public async Task BindCommandAuthoritySucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-bind", "OpenCode", "Bind"));
        var authority = await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-1", "enroll-bind", 1));
        Assert.Equal("cmd-1", authority.CommandId);
        Assert.Equal("enroll-bind", authority.EnrollmentId);
        Assert.Equal(1, authority.AuthorityGeneration);
        Assert.Equal(1, authority.Attempt);
    }

    [Fact]
    public async Task BindCommandAuthorityForSuspendedEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-bind2", "OpenCode", "Bind2"));
        await app.Store.SuspendEnrollment("enroll-bind2");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-2", "enroll-bind2", 1)));
    }

    [Fact]
    public async Task BindDuplicateCommandAuthorityUpdatesAttempt()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-dup", "OpenCode", "Dup"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-dup", "enroll-dup", 1));
        var updated = await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-dup", "enroll-dup", 2));
        Assert.Equal(2, updated.Attempt);
    }

    [Fact]
    public async Task BindCommandAuthorityWithWrongEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-bind3", "OpenCode", "Bind3"));
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-bind4", "OpenCode", "Bind4"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-wrong", "enroll-bind3", 1));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-wrong", "enroll-bind4", 1)));
    }

    [Fact]
    public async Task GetCommandAuthorityReturnsAuthority()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-get2", "OpenCode", "Get2"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-get", "enroll-get2", 1));
        var authority = await app.Store.GetCommandAuthority("cmd-get");
        Assert.NotNull(authority);
        Assert.Equal("cmd-get", authority.CommandId);
    }

    [Fact]
    public async Task AcknowledgeCommandSucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack", "OpenCode", "Ack"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack", "enroll-ack", 1));
        var acknowledged = await app.Store.AcknowledgeCommand(new AcknowledgeCommandInput("cmd-ack", "enroll-ack", "result-data"));
        Assert.NotNull(acknowledged.AcknowledgedAt);
        Assert.Equal("result-data", acknowledged.AcknowledgementData);
    }

    [Fact]
    public async Task AcknowledgeCommandWithWrongEnrollmentThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack2", "OpenCode", "Ack2"));
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack3", "OpenCode", "Ack3"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack2", "enroll-ack2", 1));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcknowledgeCommand(new AcknowledgeCommandInput("cmd-ack2", "enroll-ack3")));
    }

    [Fact]
    public async Task ValidateCommandAuthorityReturnsTrueForValid()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-valid", "OpenCode", "Valid"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-valid", "enroll-valid", 1));
        var isValid = await app.Store.ValidateCommandAuthority(new ValidateCommandAuthorityInput("cmd-valid", "enroll-valid", 1));
        Assert.True(isValid);
    }

    [Fact]
    public async Task ValidateCommandAuthorityReturnsFalseForOldGeneration()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-old", "OpenCode", "Old"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-old", "enroll-old", 1));
        await app.Store.AdvanceAuthority(new AdvanceAuthorityInput("enroll-old", 1));
        var isValid = await app.Store.ValidateCommandAuthority(new ValidateCommandAuthorityInput("cmd-old", "enroll-old", 1));
        Assert.False(isValid);
    }

    [Fact]
    public async Task AdvanceCursorSucceeds()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-cursor", "OpenCode", "Cursor"));
        var cursor = await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-cursor", "events", "cursor-1"));
        Assert.Equal("events", cursor.CursorName);
        Assert.Equal("cursor-1", cursor.CursorValue);
    }

    [Fact]
    public async Task AdvanceCursorUpdatesExisting()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-cursor2", "OpenCode", "Cursor2"));
        await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-cursor2", "events", "cursor-1"));
        var updated = await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-cursor2", "events", "cursor-2"));
        Assert.Equal("cursor-2", updated.CursorValue);
    }

    [Fact]
    public async Task AdvanceCursorWithLowerValueThrows()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-cursor3", "OpenCode", "Cursor3"));
        await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-cursor3", "events", "cursor-2"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-cursor3", "events", "cursor-1")));
    }

    [Fact]
    public async Task GetCursorReturnsCursor()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-getc", "OpenCode", "GetCursor"));
        await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-getc", "events", "cursor-x"));
        var cursor = await app.Store.GetCursor("enroll-getc", "events");
        Assert.NotNull(cursor);
        Assert.Equal("cursor-x", cursor.CursorValue);
    }

    [Fact]
    public async Task GetCursorsReturnsAllCursors()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-getc2", "OpenCode", "GetCursor2"));
        await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-getc2", "events", "e-1"));
        await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-getc2", "receipts", "r-1"));
        var cursors = await app.Store.GetCursors("enroll-getc2");
        Assert.Equal(2, cursors.Count);
    }

    [Fact]
    public async Task IsCommandAcknowledgedReturnsTrueWhenAcknowledged()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack4", "OpenCode", "Ack4"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack4", "enroll-ack4", 1));
        await app.Store.AcknowledgeCommand(new AcknowledgeCommandInput("cmd-ack4", "enroll-ack4"));
        var isAcked = await app.Store.IsCommandAcknowledged("cmd-ack4");
        Assert.True(isAcked);
    }

    [Fact]
    public async Task IsCommandAcknowledgedReturnsFalseWhenNotAcknowledged()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack5", "OpenCode", "Ack5"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack5", "enroll-ack5", 1));
        var isAcked = await app.Store.IsCommandAcknowledged("cmd-ack5");
        Assert.False(isAcked);
    }

    [Fact]
    public async Task EnrollmentSurvivesRestart()
    {
        string data, secrets, enrollmentId;
        await using (var app = new TestApp())
        {
            var enrollment = await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-restart", "OpenCode", "Restart"));
            await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-restart", "enroll-restart", 1));
            await app.Store.AdvanceCursor(new AdvanceCursorInput("enroll-restart", "events", "cursor-r"));
            enrollmentId = enrollment.Id;
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var recovered = await restarted.Store.GetEnrollment(enrollmentId);
        Assert.Equal(enrollmentId, recovered.Id);
        Assert.Equal(EnrollmentState.Active, recovered.State);
        var authority = await restarted.Store.GetCommandAuthority("cmd-restart");
        Assert.NotNull(authority);
        var cursor = await restarted.Store.GetCursor(enrollmentId, "events");
        Assert.NotNull(cursor);
        Assert.Equal("cursor-r", cursor.CursorValue);
    }

    [Fact]
    public async Task ValidateCommandAuthorityReturnsFalseForRevokedEnrollment()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-rev-val", "OpenCode", "RevVal"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-rev-val", "enroll-rev-val", 1));
        await app.Store.RevokeEnrollment("enroll-rev-val");
        var isValid = await app.Store.ValidateCommandAuthority(new ValidateCommandAuthorityInput("cmd-rev-val", "enroll-rev-val", 1));
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateCommandAuthorityReturnsFalseForSuspendedEnrollment()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-sus-val", "OpenCode", "SusVal"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-sus-val", "enroll-sus-val", 1));
        await app.Store.SuspendEnrollment("enroll-sus-val");
        var isValid = await app.Store.ValidateCommandAuthority(new ValidateCommandAuthorityInput("cmd-sus-val", "enroll-sus-val", 1));
        Assert.False(isValid);
    }

    [Fact]
    public async Task BindCommandAuthorityClearsStaleAckOnRebinding()
    {
        await using var app = new TestApp();
        await app.Store.CreateEnrollment(new CreateEnrollmentInput("enroll-ack-clear", "OpenCode", "AckClear"));
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack-clear", "enroll-ack-clear", 1));
        await app.Store.AcknowledgeCommand(new AcknowledgeCommandInput("cmd-ack-clear", "enroll-ack-clear", "old-data"));
        var before = await app.Store.GetCommandAuthority("cmd-ack-clear");
        Assert.NotNull(before!.AcknowledgedAt);
        Assert.Equal("old-data", before.AcknowledgementData);
        await app.Store.BindCommandAuthority(new BindCommandAuthorityInput("cmd-ack-clear", "enroll-ack-clear", 2));
        var after = await app.Store.GetCommandAuthority("cmd-ack-clear");
        Assert.Null(after!.AcknowledgedAt);
        Assert.Null(after.AcknowledgementData);
    }
}
