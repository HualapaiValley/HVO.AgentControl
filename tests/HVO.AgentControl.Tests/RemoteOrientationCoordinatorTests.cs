using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RemoteOrientationCoordinatorTests
{
    [Fact]
    public async Task DeliverSendsTheExactFixedMutationAndCorrelatesTheInstalledResult()
    {
        var content = "# Orientation\nhello employee\n";
        var session = new CapturingSession();
        var factory = new FixedSessionFactory(session);
        var coordinator = new RemoteOrientationCoordinator(factory);
        var artifact = new OrientationArtifact("ora-1", "emp-1", "rtb-1", null, "v1", content, "orientation-current.md", 1);

        var record = await coordinator.DeliverAsync(null!, artifact, CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal("install-orientation", session.Operation);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(session.Request);
        Assert.Equal("install-orientation", fields["operation"]);
        Assert.Equal("ora-1", fields["assignmentId"]);
        Assert.Equal("v1", fields["orientationVersion"]);
        Assert.Equal("orientation-current.md", fields["artifactFileName"]);
        Assert.Equal(content, fields["content"]);
        Assert.Equal(WorkerProtocol.OrientationContentHash(Encoding.UTF8.GetBytes(content)), fields["contentHash"]);
        Assert.Equal("installed", record.State);
        Assert.Equal(WorkerProtocol.OrientationRootDirectory + "/orientation-current.md", record.InstalledPath);
    }

    [Fact]
    public async Task DeliverRejectsPathTraversalAndOversizedContentBeforeConnecting()
    {
        var session = new CapturingSession();
        var factory = new FixedSessionFactory(session);
        var coordinator = new RemoteOrientationCoordinator(factory);
        var traversal = new OrientationArtifact("ora-1", "emp-1", "rtb-1", null, "v1", "# Orientation\n", "../escape.md", 1);
        var oversized = new OrientationArtifact("ora-2", "emp-1", "rtb-1", null, "v1", new string('x', WorkerProtocol.MaxOrientationContentBytes + 1), "orientation-current.md", 1);

        await Assert.ThrowsAsync<WorkerProtocolException>(() => coordinator.DeliverAsync(null!, traversal, CancellationToken.None));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => coordinator.DeliverAsync(null!, oversized, CancellationToken.None));
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task DeliverTreatsAnUncorrelatedAnswerAsAReconciliationIntegrityFailure()
    {
        var content = "# Orientation\n";
        var session = new CapturingSession { MismatchAssignment = true };
        var coordinator = new RemoteOrientationCoordinator(new FixedSessionFactory(session));
        var artifact = new OrientationArtifact("ora-1", "emp-1", "rtb-1", null, "v1", content, "orientation-current.md", 1);

        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => coordinator.DeliverAsync(null!, artifact, CancellationToken.None));
    }

    [Fact]
    public async Task RunComprehensionSendsTheExactFixedMutationAndBindsTheStatusRevision()
    {
        var session = new CapturingSession();
        var factory = new FixedSessionFactory(session);
        var coordinator = new RemoteOrientationCoordinator(factory);
        var status = OrientationStatusWith(revision: 7, assignment: "ora-1", employee: "emp-1", session: "ses-1", version: "v1");

        var evidence = await coordinator.RunComprehensionAsync(null!, status, CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal("orientation-comprehension", session.Operation);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(session.Request);
        Assert.Equal("orientation-comprehension", fields["operation"]);
        Assert.Equal("ora-1", fields["assignmentId"]);
        Assert.Equal("emp-1", fields["employeeId"]);
        Assert.Equal("ses-1", fields["sessionId"]);
        Assert.Equal("v1", fields["orientationVersion"]);
        Assert.Equal(7, evidence.ExpectedRevision);
        Assert.Equal("ora-1", evidence.AssignmentId);
        Assert.Equal("Operations / IT", evidence.Identity);
        Assert.Equal(["operate the control host"], evidence.Duties!);
    }

    [Fact]
    public async Task RunComprehensionRejectsUncorrelatedAnswerAndMissingSession()
    {
        var mismatched = new CapturingSession { MismatchComprehension = true };
        var coordinator = new RemoteOrientationCoordinator(new FixedSessionFactory(mismatched));
        var status = OrientationStatusWith(revision: 1, assignment: "ora-1", employee: "emp-1", session: "ses-1", version: "v1");
        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => coordinator.RunComprehensionAsync(null!, status, CancellationToken.None));

        var session = new CapturingSession();
        var factory = new FixedSessionFactory(session);
        var noSession = status with { SessionId = null };
        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => new RemoteOrientationCoordinator(factory).RunComprehensionAsync(null!, noSession, CancellationToken.None));
        Assert.Equal(0, factory.ConnectCount);
    }

    private static OrientationStatus OrientationStatusWith(int revision, string assignment, string employee, string session, string version) => new(
        employee, "rtb-1", session, assignment, revision, version, "delivered", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
        null, null, null, "orientation-current.md", 10, "policy-1", 1, null, null, false, false, [], null);

    private sealed class FixedSessionFactory(IWorkerBridgeSession session) : IWorkerBridgeSessionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken)
        {
            ConnectCount++;
            return Task.FromResult(session);
        }
    }

    private sealed class CapturingSession : IWorkerBridgeSession
    {
        public WorkerBridgeLease Lease { get; } = new(1, "controller-test", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
        public string? Operation { get; private set; }
        public object? Request { get; private set; }
        public bool MismatchAssignment { get; set; }
        public bool MismatchComprehension { get; set; }

        public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields)
        {
            Operation = operation;
            Request = new Dictionary<string, object?>(fields, StringComparer.Ordinal) { ["operation"] = operation };
            return Request;
        }

        public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
        {
            var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(request);
            if (operation == "orientation-comprehension")
            {
                var comprehension = new OrientationComprehensionRecord(
                    MismatchComprehension ? "ora-other" : (string)fields["assignmentId"]!,
                    (string)fields["employeeId"]!,
                    (string)fields["sessionId"]!,
                    (string)fields["orientationVersion"]!,
                    "Operations / IT",
                    "Operations",
                    "owner",
                    ["operate the control host"],
                    ["no secrets"],
                    "escalate uncertainty",
                    "comprehended",
                    "sha256:" + new string('a', 64),
                    false);
                return Task.FromResult(new WorkerSessionResult(operation, JsonSerializer.SerializeToElement(comprehension, WorkerProtocol.JsonOptions), mutation));
            }

            var fileName = (string)fields["artifactFileName"]!;
            var record = new OrientationInstallRecord(
                MismatchAssignment ? "ora-other" : (string)fields["assignmentId"]!,
                (string)fields["orientationVersion"]!,
                fileName,
                (string)fields["contentHash"]!,
                "installed",
                WorkerProtocol.OrientationRootDirectory + "/" + fileName,
                false);
            return Task.FromResult(new WorkerSessionResult(operation, JsonSerializer.SerializeToElement(record, WorkerProtocol.JsonOptions), mutation));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
