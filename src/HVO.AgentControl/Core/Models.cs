using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.AgentControl.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidOperationException("Invalid stored payload.");
}

public sealed class ControlOptions
{
    public string DataDirectory { get; set; } = "data";
    public string SecretsDirectory { get; set; } = ".secrets";
    public string OwnerPasswordFile { get; set; } = "owner-password";
    public bool AllowInsecureLocalHttp { get; set; }
    public int GlobalCapacity { get; set; } = 8;
    public int QueueLimit { get; set; } = 32;
    public int EventRetention { get; set; } = 10000;
    public int HistoryLimit { get; set; } = 200;
    public int PollMilliseconds { get; set; } = 750;
    public int CoordinationIdleReassessmentMinutes { get; set; } = 5;
    public int MaxPromptCharacters { get; set; } = 64000;
    public int MaxCommandRecords { get; set; } = 10000;
    public int MaxRuntimes { get; set; } = 32;
    public int MaxWorkers { get; set; } = 128;
    public string[] TrustedProxies { get; set; } = [];
}

public sealed class RuntimeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string HostKeySha256 { get; set; } = "";
    public string HostKeyAlgorithm { get; set; } = "ssh-ed25519";
    public string CredentialReference { get; set; } = "";
    public string Authentication { get; set; } = "privateKey";
    public string? PassphraseReference { get; set; }
    public string ServerPasswordReference { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string AllowedRoots { get; set; } = "";
    public string Executable { get; set; } = "";
    public int ApiPort { get; set; } = 4096;
    public bool InstallIfMissing { get; set; } = true;
    public bool PureMode { get; set; }
    public bool PrintLogs { get; set; }
    public string LogLevel { get; set; } = "";
    public int Capacity { get; set; } = 2;
    public string Labels { get; set; } = "";
    public bool DesiredConnected { get; set; }
    public string ManagedServerId { get; set; } = Guid.NewGuid().ToString("N");
    public string Transport { get; set; } = "Disconnected";
    public string Health { get; set; } = "Unknown";
    public string ProviderState { get; set; } = "Unknown";
    public string Version { get; set; } = "";
    public string InstalledExecutable { get; set; } = "";
    public string Platform { get; set; } = "";
    public string CapabilitiesJson { get; set; } = "{}";
    public string Diagnostic { get; set; } = "";
    public string ModelsJson { get; set; } = "[]";
    public long? LastHealthyAt { get; set; }
    public long? LastEventAt { get; set; }
    public int Generation { get; set; }
    public int ReconnectAttempts { get; set; }
    public long Revision { get; set; }
    public string TmuxName => "hvo-" + ManagedServerId;
}

public static class SessionRoles
{
    public const string Worker = "Worker", Coordinator = "Coordinator";
}

public sealed class WorkerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RuntimeId { get; set; } = "";
    public string ManagedServerId { get; set; } = "";
    public string NativeSessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Branch { get; set; } = "";
    public string BaseRef { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Role { get; set; } = SessionRoles.Worker;
    public string CapabilitiesJson { get; set; } = "{}";
    public string CapabilityReport { get; set; } = "";
    public long? CapabilityReportedAt { get; set; }
    public string? CapabilityCommandId { get; set; }
    public string Agent { get; set; } = "";
    public string Variant { get; set; } = "";
    public string ModelsJson { get; set; } = "[]";
    public bool Archived { get; set; }
    public long SettingsRevision { get; set; }
    public string Activity { get; set; } = "Unknown";
    public string Outcome { get; set; } = "Unassigned";
    public string CurrentAction { get; set; } = "";
    public bool Stale { get; set; } = true;
    public bool HistoryGap { get; set; }
    public long? LastObservedAt { get; set; }
    public long? LastModelAt { get; set; }
    public long? LastToolAt { get; set; }
    public long? LastStatusInquiryAt { get; set; }
    public long Revision { get; set; }
}

public static class Delivery
{
    public const string Queued = "Queued", Dispatching = "Dispatching", Accepted = "AcceptedByRuntime",
        Running = "Running", Finished = "Finished", Unknown = "DeliveryUnknown", Failed = "Failed", Cancelled = "Cancelled";
    public static bool InFlight(string state) => state is Dispatching or Accepted or Running or Unknown;
}

public sealed class CommandRecord
{
    [Key] public string Id { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string? WorkerId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string State { get; set; } = Delivery.Queued;
    public string ExecutionPayload { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public long? LastProgressAt { get; set; }
    public long? AcceptedAt { get; set; }
    public string Origin { get; set; } = "owner";
    public string? NativeMessageId { get; set; }
    public string? ResultId { get; set; }
    public string ResultJson { get; set; } = "{}";
    public string Detail { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long QueueOrder { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Dismissed { get; set; }
    public int Attempts { get; set; }
}

public sealed class AssignmentRecord
{
    [Key] public string Id { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string TemplateVersion { get; set; } = "manual-v1";
    public string Outcome { get; set; } = "Assigned";
    public string Evidence { get; set; } = "";
}

public sealed class WorkspaceClaim
{
    public string Id { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? WorkerId { get; set; }
}

public static class EnrollmentState
{
    public const string Active = "Active", Suspended = "Suspended", Revoked = "Revoked";
}

public sealed class ParticipantEnrollment
{
    [Key] public string Id { get; set; } = "";
    public string AdapterType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = EnrollmentState.Active;
    public int AuthorityGeneration { get; set; }
    public long EnrolledAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long LastSeenAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Revision { get; set; }
}

public sealed class CommandAuthority
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CommandId { get; set; } = "";
    public string EnrollmentId { get; set; } = "";
    public int AuthorityGeneration { get; set; }
    public int Attempt { get; set; } = 1;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? AcknowledgedAt { get; set; }
    public string? AcknowledgementData { get; set; }
}

public sealed class EvidenceCursor
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EnrollmentId { get; set; } = "";
    public string CursorName { get; set; } = "";
    public string CursorValue { get; set; } = "";
    public long LastConsumedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? LastAcknowledgedAt { get; set; }
}

public sealed record CreateEnrollmentInput(string Id, string AdapterType, string DisplayName);
public sealed record AdvanceAuthorityInput(string EnrollmentId, int ExpectedGeneration);
public sealed record BindCommandAuthorityInput(string CommandId, string EnrollmentId, int Attempt);
public sealed record AcknowledgeCommandInput(string CommandId, string EnrollmentId, string? AcknowledgementData = null);
public sealed record AdvanceCursorInput(string EnrollmentId, string CursorName, string CursorValue);
public sealed record ValidateCommandAuthorityInput(string CommandId, string EnrollmentId, int AuthorityGeneration);

public sealed class JournalEvent
{
    [Key] public long Sequence { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SchemaVersion { get; set; } = 1;
    public string? RuntimeId { get; set; }
    public string? WorkerId { get; set; }
    public string? CommandId { get; set; }
    public string? NativeId { get; set; }
    public string Type { get; set; } = "";
    public string Provenance { get; set; } = "service";
    public int Generation { get; set; }
    public long ObservedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Payload { get; set; } = "{}";
}

public sealed class TranscriptMessage
{
    public long Id { get; set; }
    public string WorkerId { get; set; } = "";
    public string NativeId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Json { get; set; } = "{}";
    public long NativeCreatedAt { get; set; }
}

public sealed class PendingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkerId { get; set; } = "";
    public string NativeId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string Json { get; set; } = "{}";
    public string? ReplyCommandId { get; set; }
}

public sealed record ModelChoice(string ProviderId, string ModelId, string Name, string[]? Variants = null, string[]? Agents = null);
public sealed record UpdateWorkerInput(string Id, long ExpectedRevision, string Name, string Project,
    string Description, string ProviderId, string ModelId, string Agent = "", string Variant = "");
public sealed record ReserveCoordinatorInput(string Id, long ExpectedRevision);
public sealed record DeleteRegistrationInput(string Id, long ExpectedRevision);
public sealed record WorkerArchiveInput(string Id, long ExpectedRevision, bool Archived);
public sealed record RequestId(string Id);
public sealed record RuntimeVerifyInput(RuntimeRecord Profile, string? Password = null, string? PrivateKey = null,
    string? Passphrase = null, string? TrustToken = null);
public sealed record VerificationCheck(string Name, bool Passed, string Detail, bool Required = true, bool Planned = false);
public sealed record RuntimeVerification(string Status, RuntimeRecord Profile, string Message,
    List<VerificationCheck> Checks, string? TrustToken = null, string? VerificationToken = null, string? SaveToken = null);
public sealed record VerifiedRuntimeInput(RuntimeRecord Profile, string VerificationToken, string Id);
public sealed record CreateWorkerInput(string Id, string RuntimeId, string Name, string Project, string Directory,
    string ProviderId, string ModelId, string? Repository = null, string? Branch = null, string? BaseRef = null, string Role = SessionRoles.Worker, bool DiscoverCapabilities = false);
public sealed record PromptInput(string Id, string Text, long ExpectedRevision, string? ProviderId = null,
    string? ModelId = null, bool StatusInquiry = false, string? Agent = null, string? Variant = null, bool IncludeGuidance = false, int? ProgressMinutes = null);
public sealed record ReplyInput(string Id, string RequestId, string? Permission, string[][]? Answers, bool Reject = false);
public sealed record QueueEdit(string Action);
public sealed record InspectWorkspaceInput(string Id, string RuntimeId, string Directory);
public sealed record OutcomeInput(string Outcome, string Evidence);
public sealed record ControlSnapshot(long Sequence, List<RuntimeRecord> Runtimes, List<WorkerRecord> Workers,
    List<CommandRecord> Commands, List<PendingRequest> Requests);
public sealed record WorkerDetail(WorkerRecord Worker, List<TranscriptMessage> Messages, List<CommandRecord> Commands,
    List<PendingRequest> Requests, List<AssignmentRecord> Assignments);

public sealed class ControlException(string message, int status = 409) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class CoordinationRun
{
    public string Id { get; set; } = "";
    public string CoordinatorWorkerId { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string WorkerIdsJson { get; set; } = "[]";
    public string State { get; set; } = "Ready";
    public string Detail { get; set; } = "";
    public string? DecisionCommandId { get; set; }
    public string InputJson { get; set; } = "{}";
    public string DecisionJson { get; set; } = "{}";
    public string LastObservation { get; set; } = "";
    public int Round { get; set; }
    public int MaxRounds { get; set; } = 20;
    public bool IncludeGuidance { get; set; }
    public int? ProgressMinutes { get; set; }
    public long LastDecisionAt { get; set; }
    public long Revision { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed record StartCoordinationInput(string Id, string CoordinatorWorkerId, string Instruction, string[] WorkerIds, int MaxRounds = 20, bool IncludeGuidance = false, int? ProgressMinutes = null);
public sealed record CoordinationControlInput(long ExpectedRevision, string Action);
public sealed record CoordinationPromptInput(string Id, long ExpectedRevision, string Text);
public sealed record CoordinatorDecision(string Summary, CoordinatorAction[] Actions, bool Complete = false);
public sealed record CoordinatorAction(string Type, string WorkerId, string? Text = null, string? RequestId = null, string[][]? Answers = null, bool? IncludeGuidance = null, int? ProgressMinutes = null, string? ProviderId = null, string? ModelId = null, string? Variant = null);
public sealed record CoordinatorResult(string Id, string WorkerId, string State, string Detail, string ProgressText,
    long? LastProgressAt, string Prompt, string Response, bool ResponseTruncated, bool EarlierTextOmitted);
public sealed record DecisionActionReceipt(string Type, string WorkerId, string? CommandId = null, string? RequestId = null);
public sealed record DecisionReceipt(string Summary, int Round, string DecisionCommandId, long AppliedAt, DecisionActionReceipt[] Dispatched);
public sealed record DispatchEvidence(string CommandId, string WorkerId, string Kind, string State, long CreatedAt);
public sealed record DecisionRepair(int Attempt, string RejectedCommandId);
public sealed record CoordinationRecovery(int Attempt, long RetryAt, string Reason);
public sealed record CoordinatorContext(string Instruction, WorkerRecord[] Workers, CoordinatorResult[] Results, PendingRequest[] Questions,
    DecisionReceipt? LastAppliedDecision = null, DispatchEvidence[]? Dispatch = null, DecisionRepair? Repair = null,
    CoordinationRecovery? Recovery = null, string? ReassessmentReason = null);

public sealed class OperatorUpdateSchedule
{
    [Key] public string Id { get; set; } = "";
    public string CoordinationRunId { get; set; } = "";
    public int IntervalMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    public long NextDueAt { get; set; }
    public long? LastDueAt { get; set; }
    public long LastEmittedEventSequence { get; set; }
    public long ActiveSetRevision { get; set; } = 1;
    public long Revision { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class OperatorStatusUpdate
{
    [Key] public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string ScheduleId { get; set; } = "";
    public string CoordinationRunId { get; set; } = "";
    public string Kind { get; set; } = "Scheduled";
    public long DueAt { get; set; }
    public long PublishedAt { get; set; }
    public long SourceEventSequence { get; set; }
    public long ActiveSetRevision { get; set; }
    public long MissedIntervals { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public long? AcknowledgedAt { get; set; }
}

public sealed record ConfigureOperatorUpdatesInput(string Id, int IntervalMinutes);
public sealed record OperatorParticipantSummary(string WorkerId, string Name, string Phase, string? Assignment,
    long? LastObservedAt, long? LastProgressAt, long? LastReceiptAt, string? Blocker, string NextEvent);
public sealed record OperatorStatusSummary(string CoordinationRunId, string RunState, long GeneratedAt,
    int Busy, int Available, int Queued, int Attention, OperatorParticipantSummary[] Participants);
