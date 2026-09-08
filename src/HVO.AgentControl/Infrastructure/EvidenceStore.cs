using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private const int EvidencePageLimit = 200;
    private const int EvidencePayloadCharacterBudget = 12000;

    public Task<EvidencePage> Evidence(long afterSequence, int take = 50) => Read(db => ReadEvidence(db, afterSequence, take));

    public Task<EvidencePage> ReadEvidence(EvidenceReadInput input) => Write(async db =>
    {
        ValidateConsumer(input.ConsumerId); ValidateRequestId(input.RequestId);
        ValidateEvidenceTake(input.Take);

        if (await db.EvidenceReadReceipts.FindAsync(input.RequestId) is { } prior)
        {
            if (prior.ConsumerId != input.ConsumerId) throw new ControlException("Evidence request ID belongs to another consumer.");
            return Json.Read<EvidencePage>(prior.PageJson);
        }

        if (await db.EvidenceReadReceipts.AnyAsync(x => x.ConsumerId == input.ConsumerId && x.AcknowledgedAt == null))
            throw new ControlException("A prior evidence page must be acknowledged before reading another page.");
        var cursor = await db.EvidenceConsumerCursors.FindAsync(input.ConsumerId) ?? new EvidenceConsumerCursor { ConsumerId = input.ConsumerId };
        if (db.Entry(cursor).State == EntityState.Detached) db.EvidenceConsumerCursors.Add(cursor);
        var page = await ReadEvidence(db, cursor.LastConsumedSequence, input.Take);
        db.EvidenceReadReceipts.Add(new EvidenceReadReceipt
        {
            Id = input.RequestId,
            ConsumerId = input.ConsumerId,
            AfterSequence = page.AfterSequence,
            NextSequence = page.NextSequence,
            PageJson = Json.Write(page),
            CreatedAt = Now
        });
        return page;
    });

    public Task<EvidencePage> AcknowledgeEvidence(EvidenceAcknowledgeInput input) => Write(async db =>
    {
        ValidateConsumer(input.ConsumerId); ValidateRequestId(input.RequestId);
        var receipt = await db.EvidenceReadReceipts.FindAsync(input.RequestId) ?? throw new ControlException("Evidence receipt not found.", 404);
        if (receipt.ConsumerId != input.ConsumerId) throw new ControlException("Evidence receipt belongs to another consumer.");
        var page = Json.Read<EvidencePage>(receipt.PageJson);
        if (receipt.AcknowledgedAt is not null) return page;
        if (input.ExpectedAfterSequence != receipt.AfterSequence)
            throw new ControlException("Evidence acknowledgement has a stale page boundary.");
        var cursor = await db.EvidenceConsumerCursors.FindAsync(input.ConsumerId) ?? throw new ControlException("Evidence cursor not found.");
        if (cursor.LastConsumedSequence != input.ExpectedAfterSequence)
            throw new ControlException("Evidence acknowledgement is out of order.");
        cursor.LastConsumedSequence = receipt.NextSequence;
        cursor.HistoryGap |= page.Incomplete;
        cursor.UpdatedAt = Now;
        receipt.AcknowledgedAt = Now;
        return page;
    });

    private static async Task<EvidencePage> ReadEvidence(ControlDb db, long afterSequence, int take)
    {
        if (afterSequence < 0) throw new ControlException("Evidence sequence cannot be negative.", 400);
        ValidateEvidenceTake(take);

        var earliest = await db.Events.OrderBy(x => x.Sequence).Select(x => (long?)x.Sequence).FirstOrDefaultAsync() ?? 0;
        var rows = await db.Events.AsNoTracking().Where(x => x.Sequence > afterSequence).OrderBy(x => x.Sequence)
            .Take(take + 1).ToListAsync();
        var truncated = rows.Count > take;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        var next = rows.Count == 0 ? afterSequence : rows[^1].Sequence;
        var budget = EvidencePayloadCharacterBudget;
        var events = rows.Select(row => Project(row, ref budget)).ToArray();
        return new EvidencePage(afterSequence, next, earliest, earliest > 0 && afterSequence < earliest - 1,
            truncated, events.Any(x => x.PayloadOmitted), EvidencePayloadCharacterBudget, events);
    }

    private static EvidenceEvent Project(JournalEvent row, ref int remaining)
    {
        var characters = row.Payload.Length;
        var includePayload = characters <= remaining;
        if (includePayload) remaining -= characters;
        return new EvidenceEvent(row.Sequence, row.Id, row.RuntimeId, row.WorkerId, row.CommandId, row.NativeId,
            row.Type, row.Provenance, row.Generation, row.ObservedAt, includePayload ? row.Payload : null,
            !includePayload, characters, "journal-event:" + row.Id);
    }

    private static void ValidateConsumer(string consumerId)
    {
        if (string.IsNullOrWhiteSpace(consumerId) || consumerId.Length > 120)
            throw new ControlException("Provide a stable evidence consumer ID of at most 120 characters.", 400);
    }

    private static void ValidateEvidenceTake(int take)
    {
        if (take is < 1 or > EvidencePageLimit)
            throw new ControlException($"Evidence page size must be between 1 and {EvidencePageLimit}.", 400);
    }
}
