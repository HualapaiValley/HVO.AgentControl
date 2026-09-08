using HVO.AgentControl.Core;
using HVO.AgentControl.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed class ControlDb(DbContextOptions<ControlDb> options) : DbContext(options)
{
    public DbSet<HostRecord> Hosts => Set<HostRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<InventoryMutationReceipt> InventoryMutations => Set<InventoryMutationReceipt>();
    public DbSet<RuntimeEnvironmentRecord> RuntimeEnvironments => Set<RuntimeEnvironmentRecord>();
    public DbSet<HVO.AgentControl.GitHub.GitHubAccess> GitHubAccess => Set<HVO.AgentControl.GitHub.GitHubAccess>();
    public DbSet<CoordinationRun> CoordinationRuns => Set<CoordinationRun>();
    public DbSet<RuntimeRecord> Runtimes => Set<RuntimeRecord>();
    public DbSet<WorkerRecord> Workers => Set<WorkerRecord>();
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();
    public DbSet<AssignmentRecord> Assignments => Set<AssignmentRecord>();
    public DbSet<JournalEvent> Events => Set<JournalEvent>();
    public DbSet<EvidenceConsumerCursor> EvidenceConsumerCursors => Set<EvidenceConsumerCursor>();
    public DbSet<EvidenceReadReceipt> EvidenceReadReceipts => Set<EvidenceReadReceipt>();
    public DbSet<TranscriptMessage> Messages => Set<TranscriptMessage>();
    public DbSet<ModelUsageRecord> ModelUsage => Set<ModelUsageRecord>();
    public DbSet<PendingRequest> Requests => Set<PendingRequest>();
    public DbSet<WorkspaceClaim> WorkspaceClaims => Set<WorkspaceClaim>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemPhase> WorkItemPhases => Set<WorkItemPhase>();
    public DbSet<ParticipantEnrollment> Enrollments => Set<ParticipantEnrollment>();
    public DbSet<CommandAuthority> CommandAuthorities => Set<CommandAuthority>();
    public DbSet<EvidenceCursor> EvidenceCursors => Set<EvidenceCursor>();
    public DbSet<RuntimeTelemetryHistoryRecord> TelemetryHistory => Set<RuntimeTelemetryHistoryRecord>();
    public DbSet<OperatorUpdateSchedule> OperatorUpdateSchedules => Set<OperatorUpdateSchedule>();
    public DbSet<OperatorStatusUpdate> OperatorStatusUpdates => Set<OperatorStatusUpdate>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<HostRecord>().HasKey(x => x.Sequence);
        model.Entity<HostRecord>().Property(x => x.Id).IsRequired();
        model.Entity<HostRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<HostRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<ProjectRecord>().HasKey(x => x.Sequence);
        model.Entity<ProjectRecord>().Property(x => x.Id).IsRequired();
        model.Entity<ProjectRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<ProjectRecord>().HasIndex(x => x.RepositoryUrl).IsUnique();
        model.Entity<ProjectRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<InventoryMutationReceipt>().HasKey(x => x.RequestId);
        model.Entity<RuntimeEnvironmentRecord>().HasKey(x => x.RuntimeId);
        model.Entity<RuntimeEnvironmentRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<RuntimeEnvironmentRecord>().HasOne<RuntimeRecord>().WithOne().HasForeignKey<RuntimeEnvironmentRecord>(x => x.RuntimeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RuntimeEnvironmentRecord>().HasOne<HostRecord>().WithMany().HasForeignKey(x => x.HostId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<RuntimeEnvironmentRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ConfigurationProjectId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProviderPool>();
        model.Entity<ProviderFailureReceipt>();
        model.Entity<ProviderFallbackReceipt>().HasIndex(x => x.SourceCommandId).IsUnique();
        model.Entity<HVO.AgentControl.Services.ProviderCredential>();
        model.Entity<HVO.AgentControl.Services.ProviderKeyDelivery>();
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
        model.Entity<EvidenceConsumerCursor>().HasKey(x => x.ConsumerId);
        model.Entity<EvidenceReadReceipt>().HasIndex(x => new { x.ConsumerId, x.AcknowledgedAt });
        model.Entity<WorkItem>().HasIndex(x => x.IssueNumber);
        model.Entity<WorkItem>().HasIndex(x => x.Branch);
        model.Entity<WorkItem>().HasIndex(x => new { x.Repository, x.Branch })
            .IsUnique()
            .HasFilter("State NOT IN ('Released', 'Abandoned')");
        model.Entity<WorkItem>().HasIndex(x => x.OwnerWorkerId);
        model.Entity<WorkItemPhase>().HasIndex(x => new { x.WorkItemId, x.Name }).IsUnique();
        model.Entity<ParticipantEnrollment>().HasIndex(x => x.AdapterType);
        model.Entity<ParticipantEnrollment>().HasIndex(x => x.State);
        model.Entity<CommandAuthority>().HasIndex(x => x.CommandId).IsUnique();
        model.Entity<CommandAuthority>().HasIndex(x => x.EnrollmentId);
        model.Entity<EvidenceCursor>().HasIndex(x => new { x.EnrollmentId, x.CursorName }).IsUnique();
        model.Entity<RuntimeTelemetryHistoryRecord>().HasKey(x => x.Sequence);
        model.Entity<RuntimeTelemetryHistoryRecord>().HasIndex(x => new { x.RuntimeId, x.ObservedAt, x.Sequence });
        model.Entity<OperatorUpdateSchedule>().HasIndex(x => x.CoordinationRunId).IsUnique();
        model.Entity<OperatorStatusUpdate>().Property(x => x.Id).IsRequired();
        model.Entity<OperatorStatusUpdate>().HasIndex(x => x.Id).IsUnique();
        model.Entity<OperatorStatusUpdate>().HasIndex(x => new { x.ScheduleId, x.Kind, x.DueAt }).IsUnique();
        model.Entity<OperatorStatusUpdate>().HasIndex(x => new { x.CoordinationRunId, x.Sequence });
    }
}
