using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Worker;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Delivers one approved orientation artifact to an enrolled worker over the
/// authenticated, lease-fenced bridge. It sends only the fixed
/// <c>install-orientation</c> mutation, and it accepts a success answer only when
/// the worker's durable result correlates exactly with the requested artifact.
/// </summary>
/// <remarks>
/// Readiness is deliberately out of scope: this helper establishes that the
/// artifact is durably installed in the employee home. Whether a long-lived ACP
/// process has loaded it, and how a running process is replaced to pick it up,
/// remains an orchestration and process-lifetime question. A worker that answers
/// with a mismatched identity is treated as a reconciliation-integrity failure
/// rather than a clean rejection.
/// </remarks>
public sealed class RemoteOrientationCoordinator(IWorkerBridgeSessionFactory sessions)
{
    private readonly IWorkerBridgeSessionFactory _sessions = sessions;

    /// <summary>
    /// Reads the exact worker status over a fresh authenticated session. Callers
    /// that need the current process generation and orientation-installed fields
    /// (for example, to confirm a replacement loaded the artifact) use this rather
    /// than inferring them from a prior response.
    /// </summary>
    public async Task<BridgeWorkerStatus> ReadStatusAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken)
    {
        await using var session = await _sessions.ConnectAsync(enrollment, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(session, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<BridgeWorkerStatus> ReadStatusAsync(IWorkerBridgeSession session, CancellationToken cancellationToken)
    {
        var result = await session.InvokeAsync("status", new { operation = "status" }, false, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BridgeWorkerStatus>(result.Result.GetRawText(), WorkerProtocol.JsonOptions)
            ?? throw new WorkerProtocolException("Worker status is invalid.");
    }

    public async Task<OrientationInstallRecord> DeliverAsync(WorkerEnrollmentRecord enrollment, OrientationArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        WorkerProtocol.ValidateIdentifier(artifact.AssignmentId, WorkerProtocol.MaxIdentifierLength, "orientation assignment id");
        WorkerProtocol.ValidateOrientationVersion(artifact.OrientationVersion);
        WorkerProtocol.ValidateOrientationFileName(artifact.ArtifactFileName);
        var contentHash = WorkerProtocol.OrientationContentHash(WorkerProtocol.EncodeOrientationContent(artifact.Content));

        await using var session = await _sessions.ConnectAsync(enrollment, cancellationToken).ConfigureAwait(false);
        var mutation = session.Mutation("install-orientation", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assignmentId"] = artifact.AssignmentId,
            ["orientationVersion"] = artifact.OrientationVersion,
            ["artifactFileName"] = artifact.ArtifactFileName,
            ["contentHash"] = contentHash,
            ["content"] = artifact.Content,
        });
        var result = await session.InvokeAsync("install-orientation", mutation, true, cancellationToken).ConfigureAwait(false);

        OrientationInstallRecord record;
        try
        {
            record = JsonSerializer.Deserialize<OrientationInstallRecord>(result.Result.GetRawText(), WorkerProtocol.JsonOptions)
                ?? throw new WorkerReconciliationInvalidException("Worker orientation install result is invalid.");
        }
        catch (JsonException exception)
        {
            throw new WorkerReconciliationInvalidException("Worker orientation install result is invalid.", exception);
        }

        var expectedPath = WorkerProtocol.OrientationRootDirectory + "/" + artifact.ArtifactFileName;
        if (!string.Equals(record.AssignmentId, artifact.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(record.OrientationVersion, artifact.OrientationVersion, StringComparison.Ordinal)
            || !string.Equals(record.ArtifactFileName, artifact.ArtifactFileName, StringComparison.Ordinal)
            || !string.Equals(record.ContentHash, contentHash, StringComparison.Ordinal)
            || !string.Equals(record.State, "installed", StringComparison.Ordinal)
            || !string.Equals(record.InstalledPath, expectedPath, StringComparison.Ordinal))
        {
            throw new WorkerReconciliationInvalidException("Worker orientation install result did not correlate with the requested artifact.");
        }

        return record;
    }

    /// <summary>
    /// Runs the bounded, tool-free remote comprehension turn for the delivered
    /// orientation on the exact worker process/session and returns the validated
    /// structured evidence bound to the orientation status revision. Only the
    /// fixed <c>orientation-comprehension</c> mutation is sent; the worker returns
    /// the structured JSON object and never the raw model transcript. The
    /// correlation is verified again here before the evidence is returned.
    /// </summary>
    public async Task<OrientationEvidenceRequest> RunComprehensionAsync(WorkerEnrollmentRecord enrollment, OrientationStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        WorkerProtocol.ValidateIdentifier(status.AssignmentId, WorkerProtocol.MaxIdentifierLength, "orientation assignment id");
        WorkerProtocol.ValidateIdentifier(status.EmployeeId, WorkerProtocol.MaxIdentifierLength, "orientation employee id");
        WorkerProtocol.ValidateOrientationVersion(status.OrientationVersion);
        var sessionId = status.SessionId;
        if (sessionId is null) throw new WorkerReconciliationInvalidException("The orientation status has no authoritative session to comprehend against.");
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "orientation session id");

        await using var session = await _sessions.ConnectAsync(enrollment, cancellationToken).ConfigureAwait(false);
        var mutation = session.Mutation("orientation-comprehension", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assignmentId"] = status.AssignmentId,
            ["employeeId"] = status.EmployeeId,
            ["sessionId"] = sessionId,
            ["orientationVersion"] = status.OrientationVersion,
        });
        var result = await session.InvokeAsync("orientation-comprehension", mutation, true, cancellationToken).ConfigureAwait(false);

        OrientationComprehensionRecord record;
        try
        {
            record = JsonSerializer.Deserialize<OrientationComprehensionRecord>(result.Result.GetRawText(), WorkerProtocol.JsonOptions)
                ?? throw new WorkerReconciliationInvalidException("Worker comprehension result is invalid.");
        }
        catch (JsonException exception)
        {
            throw new WorkerReconciliationInvalidException("Worker comprehension result is invalid.", exception);
        }

        if (!string.Equals(record.State, "comprehended", StringComparison.Ordinal)
            || !string.Equals(record.AssignmentId, status.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(record.EmployeeId, status.EmployeeId, StringComparison.Ordinal)
            || !string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(record.OrientationVersion, status.OrientationVersion, StringComparison.Ordinal)
            || record.EvidenceHash.Length == 0)
        {
            throw new WorkerReconciliationInvalidException("Worker comprehension result did not correlate with the requested orientation.");
        }

        return new OrientationEvidenceRequest(
            record.AssignmentId,
            record.EmployeeId,
            record.SessionId,
            record.OrientationVersion,
            record.Identity,
            record.Department,
            record.Reporting,
            record.Duties,
            record.Restrictions,
            record.Escalation,
            status.Revision);
    }
}
