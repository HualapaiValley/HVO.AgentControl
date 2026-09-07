using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed class ControlDb(DbContextOptions<ControlDb> options) : DbContext(options)
{
    public DbSet<HVO.AgentControl.GitHub.GitHubAccess> GitHubAccess => Set<HVO.AgentControl.GitHub.GitHubAccess>();
    public DbSet<CoordinationRun> CoordinationRuns => Set<CoordinationRun>();
    public DbSet<RuntimeRecord> Runtimes => Set<RuntimeRecord>();
    public DbSet<WorkerRecord> Workers => Set<WorkerRecord>();
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();
    public DbSet<AssignmentRecord> Assignments => Set<AssignmentRecord>();
    public DbSet<JournalEvent> Events => Set<JournalEvent>();
    public DbSet<TranscriptMessage> Messages => Set<TranscriptMessage>();
    public DbSet<ModelUsageRecord> ModelUsage => Set<ModelUsageRecord>();
    public DbSet<PendingRequest> Requests => Set<PendingRequest>();
    public DbSet<WorkspaceClaim> WorkspaceClaims => Set<WorkspaceClaim>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<RuntimeRecord>().Ignore(x => x.TmuxName);
        model.Entity<WorkerRecord>().HasIndex(x => new { x.RuntimeId, x.ManagedServerId, x.NativeSessionId }).IsUnique();
        model.Entity<WorkerRecord>().HasIndex(x => new { x.RuntimeId, x.Directory }).IsUnique();
        model.Entity<TranscriptMessage>().HasIndex(x => new { x.WorkerId, x.NativeId }).IsUnique();
        model.Entity<ModelUsageRecord>().HasKey(x => new { x.RuntimeId, x.NativeSessionId, x.NativeMessageId });
        model.Entity<ModelUsageRecord>().HasIndex(x => new { x.WorkerId, x.CreatedAt });
        model.Entity<ModelUsageRecord>().HasIndex(x => new { x.ProviderId, x.ModelId, x.CreatedAt });
        model.Entity<PendingRequest>().HasIndex(x => new { x.WorkerId, x.Kind, x.NativeId }).IsUnique();
        model.Entity<CommandRecord>().HasIndex(x => new { x.State, x.QueueOrder });
        model.Entity<JournalEvent>().HasIndex(x => new { x.WorkerId, x.Sequence });
    }
}
