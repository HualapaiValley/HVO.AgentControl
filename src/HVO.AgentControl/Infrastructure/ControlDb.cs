using HVO.AgentControl.Core;
using HVO.AgentControl.Provisioning;
using HVO.AgentControl.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed class ControlDb(DbContextOptions<ControlDb> options) : DbContext(options)
{
    public DbSet<ControlServiceRecord> ControlServices => Set<ControlServiceRecord>();
    public DbSet<ControlSessionBinding> ControlSessions => Set<ControlSessionBinding>();
    public DbSet<HostRecord> Hosts => Set<HostRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<InventoryMutationReceipt> InventoryMutations => Set<InventoryMutationReceipt>();
    public DbSet<RuntimeEnvironmentRecord> RuntimeEnvironments => Set<RuntimeEnvironmentRecord>();
    public DbSet<HostExecutorEnrollment> HostExecutors => Set<HostExecutorEnrollment>();
    public DbSet<HostResourcePolicy> HostResourcePolicies => Set<HostResourcePolicy>();
    public DbSet<HostResourceObservation> HostResourceObservations => Set<HostResourceObservation>();
    public DbSet<HostResourceReservation> HostResourceReservations => Set<HostResourceReservation>();
    public DbSet<HostResourceMutationReceipt> HostResourceMutations => Set<HostResourceMutationReceipt>();
    public DbSet<HVO.AgentControl.GitHub.GitHubAccess> GitHubAccess => Set<HVO.AgentControl.GitHub.GitHubAccess>();
    public DbSet<GitHubMergePolicy> GitHubMergePolicies => Set<GitHubMergePolicy>();
    public DbSet<GitHubReviewReceipt> GitHubReviewReceipts => Set<GitHubReviewReceipt>();
    public DbSet<GitHubMergeIntent> GitHubMergeIntents => Set<GitHubMergeIntent>();
    public DbSet<GitHubCheckObservation> GitHubCheckObservations => Set<GitHubCheckObservation>();
    public DbSet<GitHubMergeAttempt> GitHubMergeAttempts => Set<GitHubMergeAttempt>();
    public DbSet<GitHubMergeLease> GitHubMergeLeases => Set<GitHubMergeLease>();
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
    public DbSet<ModelUsageProvenanceRecord> ModelUsageProvenance => Set<ModelUsageProvenanceRecord>();
    public DbSet<ModelCatalogObservationRecord> ModelCatalogObservations => Set<ModelCatalogObservationRecord>();
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
    public DbSet<WorkerSlotRecord> WorkerSlots => Set<WorkerSlotRecord>();
    public DbSet<TaskWorkspaceRecord> TaskWorkspaces => Set<TaskWorkspaceRecord>();
    public DbSet<TaskSessionBindingRecord> TaskSessionBindings => Set<TaskSessionBindingRecord>();
    public DbSet<TaskBindingRecord> TaskBindings => Set<TaskBindingRecord>();
    public DbSet<ProvisionOperationRecord> ProvisionOperations => Set<ProvisionOperationRecord>();
    public DbSet<ProvisionAttemptRecord> ProvisionAttempts => Set<ProvisionAttemptRecord>();
    public DbSet<ProvisionEffectRecord> ProvisionEffects => Set<ProvisionEffectRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ControlServiceRecord>().HasOne<RuntimeRecord>().WithOne().HasForeignKey<ControlServiceRecord>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ControlServiceRecord>().HasIndex(x => x.InstanceId).IsUnique();
        model.Entity<ControlSessionBinding>().Ignore(x => x.Title);
        model.Entity<ControlSessionBinding>().Property(x => x.IsCurrent).HasDefaultValue(true).ValueGeneratedNever();
        model.Entity<ControlSessionBinding>().HasIndex(x => new { x.ScopeKind, x.ScopeId, x.Generation }).IsUnique();
        model.Entity<ControlSessionBinding>().HasIndex(x => new { x.ScopeKind, x.ScopeId }).IsUnique().HasFilter("IsCurrent = 1");
        model.Entity<ControlSessionBinding>().HasIndex(x => x.PredecessorId).IsUnique().HasFilter("PredecessorId IS NOT NULL");
        model.Entity<ControlSessionBinding>().HasIndex(x => x.WorkerId).IsUnique();
        model.Entity<ControlSessionBinding>().HasOne<ControlServiceRecord>().WithMany().HasForeignKey(x => x.ControlServiceId).OnDelete(DeleteBehavior.Restrict);
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
        model.Entity<HostExecutorEnrollment>().HasIndex(x => x.RequestId).IsUnique();
        model.Entity<HostExecutorEnrollment>().HasIndex(x => x.EndpointId).IsUnique();
        model.Entity<HostExecutorEnrollment>().HasIndex(x => x.EngineId);
        model.Entity<HostExecutorEnrollment>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<HostExecutorEnrollment>().HasOne<HostRecord>().WithMany().HasForeignKey(x => x.HostId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourcePolicy>().HasKey(x => new { x.Id, x.Revision });
        model.Entity<HostResourcePolicy>().HasIndex(x => x.RequestId).IsUnique();
        model.Entity<HostResourcePolicy>().HasIndex(x => new { x.PhysicalHostId, x.Revision }).IsUnique();
        model.Entity<HostResourcePolicy>().HasOne<HostExecutorEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceObservation>().HasIndex(x => new { x.EnrollmentId, x.Sequence }).IsUnique();
        model.Entity<HostResourceObservation>().HasIndex(x => new { x.PhysicalHostId, x.PhysicalRevision }).IsUnique();
        model.Entity<HostResourceObservation>().HasIndex(x => new { x.EnrollmentId, x.WorkspaceId, x.Sequence });
        model.Entity<HostResourceObservation>().HasOne<HostExecutorEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceReservation>().HasIndex(x => x.RequestId).IsUnique();
        model.Entity<HostResourceReservation>().HasIndex(x => new { x.PhysicalHostId, x.CanonicalWorkspaceIdentity }).IsUnique()
            .HasFilter("State IN ('Held', 'EffectCommitted', 'Unknown')");
        model.Entity<HostResourceReservation>().HasIndex(x => new { x.PhysicalHostId, x.Port }).IsUnique()
            .HasFilter("Port IS NOT NULL AND State IN ('Held', 'EffectCommitted', 'Unknown')");
        model.Entity<HostResourceReservation>().HasIndex(x => new { x.PhysicalHostId, x.SharedResourceKey }).IsUnique()
            .HasFilter("SharedResourceKey IS NOT NULL AND State IN ('Held', 'EffectCommitted', 'Unknown')");
        model.Entity<HostResourceReservation>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<HostResourceReservation>().HasOne<HostExecutorEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceReservation>().HasOne<HostResourcePolicy>().WithMany()
            .HasForeignKey(x => new { x.PolicyId, x.PolicyRevision }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceReservation>().HasOne<HostResourceObservation>().WithMany()
            .HasForeignKey(x => x.ObservationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceReservation>().HasOne<HostResourceObservation>().WithMany()
            .HasForeignKey(x => x.ReleaseObservationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<HostResourceMutationReceipt>().HasKey(x => x.RequestId);
        model.Entity<ProviderPool>();
        model.Entity<ProviderFailureReceipt>();
        model.Entity<ProviderFallbackReceipt>().HasIndex(x => x.SourceCommandId).IsUnique();
        model.Entity<HVO.AgentControl.Services.ProviderCredential>();
        model.Entity<HVO.AgentControl.Services.ProviderKeyDelivery>();
        model.Entity<HVO.AgentControl.Services.ProviderReadinessReceipt>();
        model.Entity<GitHubMergePolicy>().HasKey(x => new { x.Id, x.Revision });
        model.Entity<GitHubMergePolicy>().Property(x => x.Repository).UseCollation("NOCASE");
        model.Entity<GitHubMergePolicy>().HasIndex(x => new { x.Repository, x.BaseBranch, x.Revision }).IsUnique();
        model.Entity<GitHubReviewReceipt>().Property(x => x.Repository).UseCollation("NOCASE");
        model.Entity<GitHubReviewReceipt>().HasIndex(x => new { x.Repository, x.PullRequestNumber, x.HeadSha, x.ReviewerWorkerId }).IsUnique();
        model.Entity<GitHubMergeIntent>().Property(x => x.Repository).UseCollation("NOCASE");
        model.Entity<GitHubMergeIntent>().HasIndex(x => new { x.Repository, x.PullRequestNumber, x.ExpectedHeadSha }).IsUnique();
        model.Entity<GitHubCheckObservation>().HasIndex(x => new { x.Repository, x.PullRequestNumber, x.HeadSha, x.ObservedAt });
        model.Entity<GitHubMergeAttempt>().HasIndex(x => new { x.IntentId, x.State }).IsUnique();
        model.Entity<GitHubMergeLease>().Property(x => x.Repository).UseCollation("NOCASE");
        model.Entity<GitHubMergeLease>().HasIndex(x => new { x.Repository, x.BaseBranch }).IsUnique();
        model.Entity<ModelCatalogObservationRecord>().HasKey(x => x.RuntimeId);
        model.Entity<ModelCatalogObservationRecord>().HasOne<RuntimeRecord>().WithOne().HasForeignKey<ModelCatalogObservationRecord>(x => x.RuntimeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ModelUsageProvenanceRecord>().HasKey(x => new { x.RuntimeId, x.NativeSessionId, x.NativeMessageId });
        model.Entity<RuntimeRecord>().Ignore(x => x.TmuxName);
        model.Entity<RuntimeRecord>().Property(x => x.ConnectionKind).HasDefaultValue(RuntimeConnections.Ssh);
        model.Entity<WorkerRecord>().HasIndex(x => new { x.RuntimeId, x.ManagedServerId, x.NativeSessionId }).IsUnique();
        model.Entity<WorkerRecord>().HasIndex(x => new { x.RuntimeId, x.Directory }).IsUnique().HasFilter("Role != 'Coordinator'");
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
        model.Entity<WorkerSlotRecord>().HasKey(x => x.Sequence);
        model.Entity<WorkerSlotRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<WorkerSlotRecord>().HasIndex(x => new { x.RuntimeId, x.Name }).IsUnique();
        model.Entity<WorkerSlotRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<TaskWorkspaceRecord>().HasKey(x => x.Sequence);
        model.Entity<TaskWorkspaceRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<TaskWorkspaceRecord>().HasIndex(x => new { x.RuntimeId, x.Directory })
            .IsUnique().HasFilter("State = 'Active'");
        model.Entity<TaskWorkspaceRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<TaskWorkspaceRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskWorkspaceRecord>().HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskWorkspaceRecord>().HasOne<WorkerSlotRecord>().WithMany().HasForeignKey(x => x.WorkerSlotId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskSessionBindingRecord>().HasKey(x => x.Sequence);
        model.Entity<TaskSessionBindingRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<TaskSessionBindingRecord>().HasIndex(x => x.TaskBindingId).IsUnique();
        model.Entity<TaskSessionBindingRecord>().HasIndex(x => new { x.WorkerSlotId, x.NativeSessionId }).IsUnique().HasFilter("NativeSessionId <> ''");
        model.Entity<TaskSessionBindingRecord>().HasOne<WorkerSlotRecord>().WithMany().HasForeignKey(x => x.WorkerSlotId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskBindingRecord>().HasKey(x => x.Sequence);
        model.Entity<TaskBindingRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<TaskBindingRecord>().HasIndex(x => x.WorkItemId).IsUnique().HasFilter("State = 'Active'");
        model.Entity<TaskBindingRecord>().HasIndex(x => x.WorkerSlotId).IsUnique().HasFilter("State = 'Active'");
        model.Entity<TaskBindingRecord>().HasIndex(x => x.WorkspaceId).IsUnique().HasFilter("State = 'Active'");
        model.Entity<TaskBindingRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<TaskBindingRecord>().HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskBindingRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskBindingRecord>().HasOne<WorkerSlotRecord>().WithMany().HasForeignKey(x => x.WorkerSlotId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskBindingRecord>().HasOne<TaskWorkspaceRecord>().WithMany().HasForeignKey(x => x.WorkspaceId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskBindingRecord>().HasOne<TaskSessionBindingRecord>().WithOne().HasForeignKey<TaskSessionBindingRecord>(x => x.TaskBindingId)
            .HasPrincipalKey<TaskBindingRecord>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProvisionOperationRecord>().HasKey(x => x.Sequence);
        model.Entity<ProvisionOperationRecord>().HasIndex(x => x.Id).IsUnique();
        model.Entity<ProvisionOperationRecord>().HasIndex(x => new { x.HostId, x.State });
        model.Entity<ProvisionOperationRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<ProvisionAttemptRecord>().HasKey(x => x.OperationId);
        model.Entity<ProvisionAttemptRecord>().HasIndex(x => new { x.HostId, x.WorkspaceIdentity }).IsUnique();
        model.Entity<ProvisionAttemptRecord>().Property(x => x.Revision).IsConcurrencyToken();
        model.Entity<ProvisionAttemptRecord>().HasOne<ProvisionOperationRecord>().WithOne()
            .HasForeignKey<ProvisionAttemptRecord>(x => x.OperationId).HasPrincipalKey<ProvisionOperationRecord>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProvisionEffectRecord>().HasKey(x => new { x.OperationId, x.Effect });
        model.Entity<ProvisionEffectRecord>().HasOne<ProvisionAttemptRecord>().WithMany()
            .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
    }
}
