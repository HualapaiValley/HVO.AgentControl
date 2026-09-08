using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private const int EvidencePageLimit = 200;

    public Task<EvidencePage> Evidence(long afterSequence, int take = 50) => Read(db => ReadEvidence(db, afterSequence, take));

    public Task<EvidencePage> ConsumeEvidence(EvidenceConsumeInput input) => Write(async db =>
    {
        if (string.IsNullOrWhiteSpace(input.ConsumerId) || input.ConsumerId.Length > 120)
            throw new ControlException("Provide a stable evidence consumer ID of at most 120 characters.", 400);
        ValidateEvidenceTake(input.Take);

        var cursor = await db.EvidenceConsumerCursors.FindAsync(input.ConsumerId);
        if (cursor is null)
        {
            cursor = new EvidenceConsumerCursor { ConsumerId = input.ConsumerId };
            db.EvidenceConsumerCursors.Add(cursor);
        }

        var page = await ReadEvidence(db, cursor.LastConsumedSequence, input.Take);
        if (page.Events.Length > 0) cursor.LastConsumedSequence = page.NextSequence;
        cursor.HistoryGap |= page.Incomplete;
        cursor.UpdatedAt = Now;
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
        return new EvidencePage(afterSequence, next, earliest, earliest > 0 && afterSequence < earliest - 1,
            truncated, rows.ToArray());
    }

    private static void ValidateEvidenceTake(int take)
    {
        if (take is < 1 or > EvidencePageLimit)
            throw new ControlException($"Evidence page size must be between 1 and {EvidencePageLimit}.", 400);
    }
}
