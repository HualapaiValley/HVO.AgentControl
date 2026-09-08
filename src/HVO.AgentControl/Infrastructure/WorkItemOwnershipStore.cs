using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<WorkItem> CreateWorkItem(CreateWorkItemInput input) => Write(async db =>
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 500)
            throw new ControlException("Work item title is required and must be 500 characters or fewer.", 400);
        if (string.IsNullOrWhiteSpace(input.Branch) || input.Branch.Length > 200)
            throw new ControlException("Work item branch is required and must be 200 characters or fewer.", 400);
        if (string.IsNullOrWhiteSpace(input.Repository) || input.Repository.Length > 500)
            throw new ControlException("Repository identifier is required and must be 500 characters or fewer.", 400);
        if (string.IsNullOrEmpty(input.WorkerId) == string.IsNullOrEmpty(input.WorkerSlotId))
            throw new ControlException("Choose exactly one owner worker or reusable worker slot.", 400);
        WorkerRecord? owner = null;
        WorkerSlotRecord? slot = null;
        if (!string.IsNullOrEmpty(input.WorkerId))
        {
            owner = await db.Workers.FindAsync(input.WorkerId) ?? throw new ControlException("Owner worker not found.", 404);
            if (owner.Archived) throw new ControlException("Cannot assign work to an archived worker.", 400);
        }
        else
        {
            slot = await db.WorkerSlots.SingleOrDefaultAsync(x => x.Id == input.WorkerSlotId) ?? throw new ControlException("Owner worker slot not found.", 404);
            if (slot.Archived || slot.Role != SessionRoles.Worker) throw new ControlException("Cannot assign work to an unavailable worker slot.", 409);
        }
        var existing = await db.WorkItems.FindAsync(input.Id);
        if (existing is not null) throw new ControlException("Work item ID already exists.", 409);
        var liveConflict = await db.WorkItems.FirstOrDefaultAsync(x =>
            x.Repository == input.Repository && x.Branch == input.Branch &&
            x.State != WorkItemState.Released && x.State != WorkItemState.Abandoned);
        if (liveConflict is not null)
            throw new ControlException($"Branch '{input.Branch}' in repository '{input.Repository}' already has an active work item '{liveConflict.Id}' (issue #{liveConflict.IssueNumber}).");
        var workItem = new WorkItem
        {
            Id = input.Id,
            IssueNumber = input.IssueNumber,
            Title = input.Title,
            Branch = input.Branch,
            Repository = input.Repository,
            OwnerWorkerId = owner?.Id ?? "",
            OwnerWorkerSlotId = slot?.Id,
            State = WorkItemState.Active,
            CurrentPhase = input.PhaseName ?? "implementation"
        };
        db.WorkItems.Add(workItem);
        var phaseName = input.PhaseName ?? "implementation";
        db.WorkItemPhases.Add(new WorkItemPhase
        {
            WorkItemId = input.Id,
            Name = phaseName,
            State = WorkItemPhaseState.Active,
            OwnerWorkerId = owner?.Id ?? "",
            OwnerWorkerSlotId = slot?.Id,
            StartedAt = ControlStore.Now
        });
        Event(db, "WorkItemCreated", slot?.RuntimeId, payload: new { workItem.Id, workItem.OwnerWorkerId, workItem.OwnerWorkerSlotId }, provenance: "user");
        return workItem;
    });

    public Task<WorkItem> ClaimWorkItem(WorkItemClaimInput input) => Write(async db =>
    {
        var workItem = await db.WorkItems.FindAsync(input.WorkItemId) ?? throw new ControlException("Work item not found.", 404);
        if (workItem.State == WorkItemState.Released || workItem.State == WorkItemState.Abandoned)
            throw new ControlException($"Work item is {workItem.State.ToString().ToLowerInvariant()} and cannot be claimed.");
        if (workItem.OwnerWorkerId != input.WorkerId)
        {
            var existingOwner = await db.Workers.FindAsync(workItem.OwnerWorkerId);
            throw new ControlException($"Work item is owned by '{(existingOwner?.Name ?? "unknown")}'. Only the owner may claim it.");
        }
        workItem.UpdatedAt = ControlStore.Now;
        workItem.Revision++;
        if (input.PhaseName is not null && input.PhaseName != workItem.CurrentPhase)
        {
            var existingPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.WorkItemId && x.Name == input.PhaseName);
            if (existingPhase is null)
            {
                var priorPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.WorkItemId && x.Name == workItem.CurrentPhase);
                if (priorPhase is { State: WorkItemPhaseState.Active })
                {
                    priorPhase.State = WorkItemPhaseState.Complete;
                    priorPhase.CompletedAt = ControlStore.Now;
                }
                db.WorkItemPhases.Add(new WorkItemPhase
                {
                    WorkItemId = input.WorkItemId,
                    Name = input.PhaseName,
                    State = WorkItemPhaseState.Active,
                    OwnerWorkerId = input.WorkerId,
                    StartedAt = ControlStore.Now
                });
                workItem.CurrentPhase = input.PhaseName;
            }
            else if (existingPhase.State == WorkItemPhaseState.Pending)
            {
                existingPhase.State = WorkItemPhaseState.Active;
                existingPhase.OwnerWorkerId = input.WorkerId;
                existingPhase.StartedAt = ControlStore.Now;
                workItem.CurrentPhase = input.PhaseName;
            }
        }
        Event(db, "WorkItemClaimed", payload: new { workItem.Id, workItem.CurrentPhase }, provenance: "user");
        return workItem;
    });

    public Task<WorkItem> ReleaseWorkItem(WorkItemReleaseInput input) => Write(async db =>
    {
        var workItem = await db.WorkItems.FindAsync(input.WorkItemId) ?? throw new ControlException("Work item not found.", 404);
        if (workItem.OwnerWorkerId != input.WorkerId)
        {
            var existingOwner = await db.Workers.FindAsync(workItem.OwnerWorkerId);
            throw new ControlException($"Work item is owned by '{(existingOwner?.Name ?? "unknown")}'. Only the owner may release it.");
        }
        if (workItem.State == WorkItemState.Released || workItem.State == WorkItemState.Abandoned)
            throw new ControlException($"Work item is {workItem.State.ToString().ToLowerInvariant()} and cannot be released.");
        if (input.PhaseName is not null)
        {
            var phase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.WorkItemId && x.Name == input.PhaseName);
            if (phase is not null && phase.State == WorkItemPhaseState.Active)
            {
                phase.State = WorkItemPhaseState.Complete;
                phase.CompletedAt = ControlStore.Now;
                phase.Evidence = input.Evidence;
            }
        }
        workItem.State = WorkItemState.Released;
        workItem.UpdatedAt = ControlStore.Now;
        workItem.Revision++;
        Event(db, "WorkItemReleased", payload: new { workItem.Id }, provenance: "user");
        return workItem;
    });

    public Task<WorkItem> TransitionWorkItem(TransitionWorkItemInput input) => Write(async db =>
    {
        var workItem = await db.WorkItems.FindAsync(input.Id) ?? throw new ControlException("Work item not found.", 404);
        if (workItem.OwnerWorkerId != input.WorkerId)
        {
            var existingOwner = await db.Workers.FindAsync(workItem.OwnerWorkerId);
            throw new ControlException($"Work item is owned by '{(existingOwner?.Name ?? "unknown")}'. Only the owner may transition it.");
        }
        if (workItem.State == WorkItemState.Released || workItem.State == WorkItemState.Abandoned)
            throw new ControlException($"Work item is {workItem.State.ToString().ToLowerInvariant()} and cannot be transitioned.");
        if (workItem.Revision != input.ExpectedRevision) throw new ControlException("Work item changed; refresh and retry.", 409);
        var validStates = new[] { WorkItemState.Active, WorkItemState.InReview, WorkItemState.InCI, WorkItemState.Completed, WorkItemState.Released, WorkItemState.Abandoned };
        if (!validStates.Contains(input.State))
            throw new ControlException($"Invalid state '{input.State}'. Valid states: {string.Join(", ", validStates)}.", 400);
        var validTransitions = new Dictionary<string, string[]>
        {
            [WorkItemState.Active] = new[] { WorkItemState.InReview, WorkItemState.InCI, WorkItemState.Completed, WorkItemState.Abandoned },
            [WorkItemState.InReview] = new[] { WorkItemState.InCI, WorkItemState.Active, WorkItemState.Completed, WorkItemState.Abandoned },
            [WorkItemState.InCI] = new[] { WorkItemState.Completed, WorkItemState.InReview, WorkItemState.Abandoned },
            [WorkItemState.Completed] = new[] { WorkItemState.Released },
            [WorkItemState.Released] = Array.Empty<string>(),
            [WorkItemState.Abandoned] = Array.Empty<string>()
        };
        if (!validTransitions.TryGetValue(workItem.State, out var allowed) || !allowed.Contains(input.State))
            throw new ControlException($"Cannot transition from '{workItem.State}' to '{input.State}'.");
        workItem.State = input.State;
        if (input.PhaseName is not null)
        {
            var currentPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.Id && x.State == WorkItemPhaseState.Active);
            if (currentPhase is not null)
            {
                currentPhase.State = WorkItemPhaseState.Complete;
                currentPhase.CompletedAt = ControlStore.Now;
                currentPhase.Evidence = input.Evidence;
            }
            var nextPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.Id && x.Name == input.PhaseName);
            if (nextPhase is null)
            {
                db.WorkItemPhases.Add(new WorkItemPhase
                {
                    WorkItemId = input.Id,
                    Name = input.PhaseName,
                    State = WorkItemPhaseState.Active,
                    OwnerWorkerId = workItem.OwnerWorkerId,
                    StartedAt = ControlStore.Now,
                    Evidence = input.Evidence
                });
            }
            else
            {
                nextPhase.State = WorkItemPhaseState.Active;
                nextPhase.StartedAt = ControlStore.Now;
                nextPhase.Evidence = input.Evidence;
            }
            workItem.CurrentPhase = input.PhaseName;
        }
        workItem.UpdatedAt = ControlStore.Now;
        workItem.Revision++;
        Event(db, "WorkItemTransitioned", payload: new { workItem.Id, workItem.State, workItem.CurrentPhase }, provenance: "user");
        return workItem;
    });

    public Task<WorkItem> AdvancePhase(AdvancePhaseInput input) => Write(async db =>
    {
        var workItem = await db.WorkItems.FindAsync(input.WorkItemId) ?? throw new ControlException("Work item not found.", 404);
        if (workItem.OwnerWorkerId != input.WorkerId) throw new ControlException("Only the work item owner may advance phases.", 403);
        if (workItem.State == WorkItemState.Released || workItem.State == WorkItemState.Abandoned) throw new ControlException($"Work item is {workItem.State.ToString().ToLowerInvariant()} and cannot be modified.", 409);
        var fromPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.WorkItemId && x.Name == input.FromPhase && x.State == WorkItemPhaseState.Active);
        if (fromPhase is null) throw new ControlException($"Active phase '{input.FromPhase}' not found.", 404);
        var toPhase = await db.WorkItemPhases.FirstOrDefaultAsync(x => x.WorkItemId == input.WorkItemId && x.Name == input.ToPhase);
        if (toPhase is null)
        {
            toPhase = new WorkItemPhase
            {
                WorkItemId = input.WorkItemId,
                Name = input.ToPhase,
                State = WorkItemPhaseState.Active,
                OwnerWorkerId = input.WorkerId,
                StartedAt = ControlStore.Now,
                Evidence = input.Evidence
            };
            db.WorkItemPhases.Add(toPhase);
        }
        else
        {
            if (toPhase.State != WorkItemPhaseState.Pending && toPhase.State != WorkItemPhaseState.Skipped)
                throw new ControlException($"Phase '{input.ToPhase}' is already {(toPhase.State == WorkItemPhaseState.Active ? "active" : "complete")}.");
            toPhase.State = WorkItemPhaseState.Active;
            toPhase.OwnerWorkerId = input.WorkerId;
            toPhase.StartedAt = ControlStore.Now;
            toPhase.Evidence = input.Evidence;
        }
        fromPhase.State = WorkItemPhaseState.Complete;
        fromPhase.CompletedAt = ControlStore.Now;
        fromPhase.Evidence = input.Evidence;
        workItem.CurrentPhase = input.ToPhase;
        workItem.UpdatedAt = ControlStore.Now;
        workItem.Revision++;
        Event(db, "WorkItemPhaseAdvanced", payload: new { workItem.Id, FromPhase = input.FromPhase, ToPhase = input.ToPhase }, provenance: "user");
        return workItem;
    });

    public Task<WorkItem?> GetWorkItem(string id) => Read(db => db.WorkItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));

    public Task<List<WorkItem>> GetWorkItemsByOwner(string workerId) => Read(db => db.WorkItems.AsNoTracking().Where(x => x.OwnerWorkerId == workerId).ToListAsync());

    public Task<List<WorkItem>> GetActiveWorkItems() => Read(db => db.WorkItems.AsNoTracking().Where(x => x.State != WorkItemState.Released && x.State != WorkItemState.Abandoned).ToListAsync());

    public Task<List<WorkItemPhase>> GetPhases(string workItemId) => Read(db => db.WorkItemPhases.AsNoTracking().Where(x => x.WorkItemId == workItemId).OrderBy(x => x.CreatedAt).ToListAsync());

    public Task<bool> IsWorkItemOwner(string workItemId, string workerId)
    {
        return Read(db => db.WorkItems.AnyAsync(x => x.Id == workItemId && x.OwnerWorkerId == workerId));
    }

    public Task<bool> ValidateOneModifyingOwner(string workItemId, string workerId)
    {
        return Read(async db =>
        {
            var workItem = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId && w.OwnerWorkerId == workerId);
            if (workItem is null) return false;
            if (workItem.State == WorkItemState.Released || workItem.State == WorkItemState.Abandoned) return false;
            return await db.Workers.AnyAsync(w => w.Id == workerId && !w.Archived);
        });
    }
}
