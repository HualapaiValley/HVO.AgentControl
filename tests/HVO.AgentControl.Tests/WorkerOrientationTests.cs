using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerOrientationTests
{
    [Fact]
    public async Task DispatchInstallOrientationRecordsDurableInstalledStateAndIdempotentReplay()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        var installer = new RecordingInstaller();
        await using var runtime = new WorkerRuntime(store, new MemoryStream(), new MemoryStream(), null, installer);
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        var content = "# Orientation\nhello employee\n";
        var hash = Hash(content);

        using var first = InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", content);
        await using var firstResponse = new MemoryStream();
        await bridge.DispatchAsync(firstResponse, first.RootElement, lease, CancellationToken.None);
        var installed = ReadResult(firstResponse);
        var expectedPath = WorkerProtocol.OrientationRootDirectory + "/orientation-current.md";
        Assert.Equal("installed", installed.State);
        Assert.Equal("ora-1", installed.AssignmentId);
        Assert.Equal("v1", installed.OrientationVersion);
        Assert.Equal("orientation-current.md", installed.ArtifactFileName);
        Assert.Equal(hash, installed.ContentHash);
        Assert.Equal(expectedPath, installed.InstalledPath);
        Assert.False(installed.AlreadyInstalled);
        Assert.Single(installer.Calls);

        using var replay = InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", content);
        await using var replayResponse = new MemoryStream();
        await bridge.DispatchAsync(replayResponse, replay.RootElement, lease, CancellationToken.None);
        var replayed = ReadResult(replayResponse);
        Assert.True(replayed.AlreadyInstalled);
        Assert.Equal("installed", replayed.State);
        Assert.Single(installer.Calls);

        var status = store.Status();
        Assert.Equal("ora-1", status.OrientationAssignmentId);
        Assert.Equal("v1", status.OrientationVersion);
        Assert.Equal("orientation-current.md", status.OrientationArtifactFileName);
        Assert.Equal(hash, status.OrientationContentHash);
        Assert.Equal("installed", status.OrientationState);
        Assert.Equal(expectedPath, status.OrientationInstalledPath);
    }

    [Fact]
    public async Task DispatchInstallOrientationRejectsConflictsAttacksAndExactFieldViolationsBeforeWrite()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        var installer = new RecordingInstaller();
        await using var runtime = new WorkerRuntime(store, new MemoryStream(), new MemoryStream(), null, installer);
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        var content = "# Orientation\n";
        var hash = Hash(content);

        // Establish one installed artifact, then prove the same assignment cannot be repointed.
        using (var seed = InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", content))
        {
            await bridge.DispatchAsync(new MemoryStream(), seed.RootElement, lease, CancellationToken.None);
        }

        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v2", "orientation-current.md", content));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-2", "v1", "../escape.md", content));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-3", "v1", "sub/dir.md", content));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-4", "v1", "orientation-current.md", content, hash: "sha256:" + new string('0', 64)));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-5", "v1", "orientation-current.md", "bad\0content"));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-6", "v1", "orientation-current.md", content, extra: "unexpected"));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-7", "v" + new string('a', 128), "orientation-current.md", content));
        await AssertRejected(bridge, lease, InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-8", " ", "orientation-current.md", content));

        Assert.Single(installer.Calls);
    }

    [Fact]
    public async Task DispatchInstallOrientationFailureBecomesUncertainAndCanBeRetried()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        var installer = new RecordingInstaller { Fail = true };
        await using var runtime = new WorkerRuntime(store, new MemoryStream(), new MemoryStream(), null, installer);
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        var content = "# Orientation\n";
        var hash = Hash(content);

        using var first = InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", content);
        await Assert.ThrowsAsync<WorkerOperationUncertainException>(() => bridge.DispatchAsync(new MemoryStream(), first.RootElement, lease, CancellationToken.None));
        Assert.Equal("uncertain", store.Status().OrientationState);

        installer.Fail = false;
        using var retry = InstallMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", content);
        await using var response = new MemoryStream();
        await bridge.DispatchAsync(response, retry.RootElement, lease, CancellationToken.None);
        Assert.Equal("installed", ReadResult(response).State);
        Assert.Equal("installed", store.Status().OrientationState);
    }

    [Fact]
    public void WorkerStatusOrientationFieldsRoundTripThroughControllerDeserialization()
    {
        using var temp = new WorkerTemp();
        using var store = new WorkerStore(temp.Options());
        var lease = store.AcquireLease("controller-test", Nonce(1));
        var content = "# Orientation\n";
        var hash = Hash(content);
        var path = WorkerProtocol.OrientationRootDirectory + "/orientation-current.md";
        store.BeginOrientationInstall(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", hash);
        store.CompleteOrientationInstall(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", hash, path);

        var element = JsonSerializer.SerializeToElement(store.Status(), WorkerProtocol.JsonOptions);
        var bridge = JsonSerializer.Deserialize<BridgeWorkerStatus>(element.GetRawText(), WorkerProtocol.JsonOptions)!;
        Assert.Equal("ora-1", bridge.OrientationAssignmentId);
        Assert.Equal("v1", bridge.OrientationVersion);
        Assert.Equal("orientation-current.md", bridge.OrientationArtifactFileName);
        Assert.Equal(hash, bridge.OrientationContentHash);
        Assert.Equal("installed", bridge.OrientationState);
        Assert.Equal(path, bridge.OrientationInstalledPath);
    }

    [Fact]
    public void StoreMigratesExactSchemaV9AndAddsUsableOrientationArtifacts()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using (var store = new WorkerStore(options)) { }
        var db = System.IO.Path.Combine(temp.Path, "bridge.db");
        using (var connection = Open(db))
        {
            connection.Execute("DROP TABLE orientation_artifacts; DROP TABLE orientation_comprehension; UPDATE meta SET value='9' WHERE key='schema_version'; UPDATE meta SET value='hvo-worker-bridge-v9-20260916' WHERE key='schema_signature'; PRAGMA wal_checkpoint(TRUNCATE)");
        }

        using (var migrated = new WorkerStore(options))
        {
            using var connection = Open(db);
            Assert.Equal("11", Scalar(connection, "SELECT value FROM meta WHERE key='schema_version'"));
            Assert.Equal("hvo-worker-bridge-v11-20260919", Scalar(connection, "SELECT value FROM meta WHERE key='schema_signature'"));
            var lease = migrated.AcquireLease("controller-test", Nonce(1));
            var hash = Hash("# Orientation\n");
            migrated.BeginOrientationInstall(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", "orientation-current.md", hash);
            migrated.CompleteOrientationInstall(lease.Epoch, lease.ConnectionNonce, "ora-1", "v1", hash, WorkerProtocol.OrientationRootDirectory + "/orientation-current.md");
            Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM orientation_artifacts")));
        }
    }

    [Fact]
    public async Task SupervisorInstallerUsesExactFixedFrameAndRejectsUncorrelatedAnswer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var content = "# Orientation\n";
        var hash = Hash(content);

        var success = await RunSupervisorAsync(hash, expectedPath: WorkerProtocol.OrientationRootDirectory + "/orientation-current.md");
        Assert.Equal(WorkerProtocol.OrientationRootDirectory + "/orientation-current.md", success.InstalledPath);
        Assert.Equal(hash, success.ContentHash);

        await Assert.ThrowsAsync<WorkerProtocolException>(() => RunSupervisorAsync(hash, expectedPath: "/tmp/elsewhere.md"));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => RunSupervisorAsync("sha256:" + new string('0', 64), expectedPath: WorkerProtocol.OrientationRootDirectory + "/orientation-current.md"));
    }

    private static async Task<OrientationInstallReceipt> RunSupervisorAsync(string responseHash, string expectedPath)
    {
        var socketPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hvo-supervisor-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var content = "# Orientation\n";
        var installer = new WorkerOrientationInstaller(socketPath);
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync().ConfigureAwait(false);
            using var stream = new NetworkStream(accepted);
            using var frame = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None).ConfigureAwait(false);
            var root = frame!.RootElement;
            Assert.Equal("orientation-install", root.GetProperty("operation").GetString());
            Assert.Equal("ora-1", root.GetProperty("assignmentId").GetString());
            Assert.Equal("v1", root.GetProperty("orientationVersion").GetString());
            Assert.Equal("orientation-current.md", root.GetProperty("artifactFileName").GetString());
            Assert.Equal(content, Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("content").GetString()!)));
            await WorkerProtocol.WriteFrameAsync(stream, new { ok = true, installedPath = expectedPath, contentHash = responseHash }, CancellationToken.None).ConfigureAwait(false);
        });
        try
        {
            return await installer.InstallAsync("ora-1", "v1", "orientation-current.md", Encoding.UTF8.GetBytes(content), WorkerProtocol.OrientationContentHash(Encoding.UTF8.GetBytes(content)), CancellationToken.None);
        }
        finally
        {
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            try { File.Delete(socketPath); } catch { }
        }
    }

    [Fact]
    public async Task DispatchComprehensionReturnsValidatedEvidenceAndNeverJournalsRawText()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\nsecret-standing-fact\n");
        var installer = new RecordingInstaller { Content = Encoding.UTF8.GetBytes("# Orientation\nsecret-standing-fact\n") };
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);

        using var message = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        var dispatch = bridge.DispatchAsync(new MemoryStream(), message.RootElement, lease, CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prompt = JsonDocument.Parse(output.Text).RootElement;
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        var promptText = prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString()!;
        Assert.Contains("secret-standing-fact", promptText, StringComparison.Ordinal);
        var acpId = prompt.GetProperty("id").GetInt64();

        var evidence = EvidenceJson("ora-1", "emp-1", "ses-test", "v1");
        var midpoint = evidence.Length / 2;
        input.Enqueue(ChunkFrame("ses-test", evidence[..midpoint]));
        input.Enqueue(ChunkFrame("ses-test", evidence[midpoint..]));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");
        try { await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            var st = store.Status();
            throw new Xunit.Sdk.XunitException($"DIAG output={output.Text.Replace("\n", " | ")} state={st.ProcessState} acp={st.AcpInitialized} orient={st.OrientationState} comp={st.OrientationComprehensionState} session={st.SessionId} dispatchState={(dispatch.IsFaulted ? dispatch.Exception?.ToString() : dispatch.Status.ToString())}");
        }

        var status = store.Status();
        Assert.Equal("comprehended", status.OrientationComprehensionState);
        Assert.NotNull(status.OrientationComprehensionEvidenceHash);
        Assert.DoesNotContain("secret-standing-fact", JsonSerializer.Serialize(status, WorkerProtocol.JsonOptions), StringComparison.Ordinal);
        // No journaled event carries raw response text or prompt content.
        foreach (var item in store.Replay(status.WorkerGeneration, 0).Events)
            Assert.DoesNotContain("secret-standing-fact", item.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchComprehensionReplayReturnsRetainedEvidenceWithoutSecondAcpRequest()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        var installer = new RecordingInstaller();
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);

        using (var first = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1"))
        {
            var dispatch = bridge.DispatchAsync(new MemoryStream(), first.RootElement, lease, CancellationToken.None);
            await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
            input.Enqueue(ChunkFrame("ses-test", EvidenceJson("ora-1", "emp-1", "ses-test", "v1")));
            input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Single(output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        using var replay = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        await using var replayResponse = new MemoryStream();
        await bridge.DispatchAsync(replayResponse, replay.RootElement, lease, CancellationToken.None);
        using var document = JsonDocument.Parse(replayResponse.ToArray());
        Assert.Equal("comprehended", document.RootElement.GetProperty("result").GetProperty("state").GetString());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("alreadyComprehended").GetBoolean());
        // No second model turn was issued.
        Assert.Single(output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Single(installer.Reads);
    }

    [Fact]
    public async Task DispatchComprehensionRefusesRunningOrUncertainOperationWithoutIssuingPrompt()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        var installer = new RecordingInstaller();
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
        store.BeginOrientationComprehension(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");

        using var running = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        await Assert.ThrowsAsync<WorkerOperationUncertainException>(() => bridge.DispatchAsync(new MemoryStream(), running.RootElement, lease, CancellationToken.None));
        Assert.Equal(string.Empty, output.Text);

        // A post-write uncertainty is also refused until reconciled.
        store.MarkOrientationComprehensionUncertain("ora-1", "emp-1", "ses-test", "v1");
        using var uncertain = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        await Assert.ThrowsAsync<WorkerOperationUncertainException>(() => bridge.DispatchAsync(new MemoryStream(), uncertain.RootElement, lease, CancellationToken.None));
        Assert.Equal(string.Empty, output.Text);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("overflow")]
    [InlineData("fenced")]
    [InlineData("wrong-assignment")]
    [InlineData("missing-field")]
    public async Task DispatchComprehensionRejectsMalformedOrUncorrelatedEvidence(string scenario)
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        var installer = new RecordingInstaller();
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);

        var text = scenario switch
        {
            "malformed" => "{not-json",
            "overflow" => new string('x', WorkerProtocol.MaxOrientationComprehensionBytes + 1),
            "fenced" => "```json\n" + EvidenceJson("ora-1", "emp-1", "ses-test", "v1") + "\n```",
            "wrong-assignment" => EvidenceJson("ora-other", "emp-1", "ses-test", "v1"),
            "missing-field" => "{\"assignmentId\":\"ora-1\",\"employeeId\":\"emp-1\",\"sessionId\":\"ses-test\",\"orientationVersion\":\"v1\"}",
            _ => throw new InvalidOperationException(),
        };

        using var message = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        var dispatch = bridge.DispatchAsync(new MemoryStream(), message.RootElement, lease, CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        input.Enqueue(ChunkFrame("ses-test", text));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");
        await Assert.ThrowsAsync<WorkerProtocolException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("failed", store.Status().OrientationComprehensionState);
        Assert.Null(store.Status().OrientationComprehensionEvidenceHash);
    }

    [Fact]
    public async Task DispatchComprehensionRejectsUncorrelatedSessionChunksAndNonEndTurn()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        var installer = new RecordingInstaller();
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);

        using var message = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        var dispatch = bridge.DispatchAsync(new MemoryStream(), message.RootElement, lease, CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        // A chunk for a different session must never be captured or seal the turn.
        input.Enqueue(ChunkFrame("ses-other", EvidenceJson("ora-1", "emp-1", "ses-test", "v1")));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"max_tokens\"}}}}\n");
        await Assert.ThrowsAsync<WorkerProtocolException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("failed", store.Status().OrientationComprehensionState);
    }

    [Fact]
    public async Task DispatchComprehensionUnboundPermissionRequestIsRejected()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        var installer = new RecordingInstaller();
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store, installer);
        runtime.Start();
        await using var bridge = new WorkerBridge(options, store, runtime, new byte[32]);
        var lease = store.AcquireLease("controller-test", Nonce(1));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);

        using var message = ComprehensionMessage(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
        var dispatch = bridge.DispatchAsync(new MemoryStream(), message.RootElement, lease, CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        input.Enqueue("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"ses-test\",\"options\":[{\"optionId\":\"once\",\"kind\":\"allow_once\"}]}}\n");
        input.Enqueue(ChunkFrame("ses-test", EvidenceJson("ora-1", "emp-1", "ses-test", "v1")));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        // A tool/permission request without an owner submission is cancelled, never
        // selected as allow, and no pending permission is retained.
        Assert.Null(store.Status().PendingPermission);
        var frames = output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(frame => JsonDocument.Parse(frame)).ToArray();
        var permissionResponse = frames.Single(frame => frame.RootElement.TryGetProperty("id", out var id) && id.GetInt64() == 99);
        Assert.Equal("cancelled", permissionResponse.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TaskSubmitCapturesCanonicalReportAndNeverJournalsRawText()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store);
        runtime.Start();
        var lease = store.AcquireLease("controller-test", Nonce(2));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
        using var envelope = JsonDocument.Parse("""{"method":"session/prompt","params":{"sessionId":"ses-test","prompt":[{"type":"text","text":"bounded task"}]}}""");

        var forwarded = await runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req-task-report", envelope.RootElement, "turn-task-report", CancellationToken.None, captureTaskReport: true);
        Assert.Equal("forwarded", forwarded.State);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var submitted = JsonDocument.Parse(output.Text);
        var acpId = submitted.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(JsonValueKind.Array, submitted.RootElement.GetProperty("params").GetProperty("prompt").ValueKind);
        var report = """{"summary":"done","changedPaths":["src/b.cs","src/a.cs"],"tests":[{"recipeId":"dotnet-test-release","status":"passed","summary":"all passed"}],"deniedAction":null,"limitations":[]}""";
        input.Enqueue(ChunkFrame("ses-other", "raw-secret-ignored"));
        input.Enqueue(ChunkFrame("ses-test", report[..40]));
        input.Enqueue(ChunkFrame("ses-test", report[40..]));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");

        await WaitForRequestState(store, "req-task-report", "completed");
        var completed = store.GetRequest("req-task-report")!;
        Assert.Equal("""{"summary":"done","changedPaths":["src/a.cs","src/b.cs"],"tests":[{"recipeId":"dotnet-test-release","status":"passed","summary":"all passed"}],"deniedAction":null,"limitations":[]}""", completed.OutcomeJson);
        foreach (var item in store.Replay(store.WorkerGeneration, 0).Events)
        {
            Assert.DoesNotContain("raw-secret-ignored", item.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("all passed", item.PayloadJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TaskSubmitUsesTheFinalCanonicalReportAfterBoundedProgressText()
    {
        using var temp = new WorkerTemp();
        using var store = new WorkerStore(temp.Options());
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store);
        runtime.Start();
        var lease = store.AcquireLease("controller-test", Nonce(22));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
        using var envelope = JsonDocument.Parse("""{"method":"session/prompt","params":{"sessionId":"ses-test","prompt":[{"type":"text","text":"bounded task"}]}}""");

        await runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req-task-progress", envelope.RootElement, "turn-task-progress", CancellationToken.None, captureTaskReport: true);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        var report = """{"summary":"done","changedPaths":["src/a.cs"],"tests":[],"deniedAction":null,"limitations":[]}""";
        input.Enqueue(ChunkFrame("ses-test", "Working on the bounded files.\n"));
        input.Enqueue(ChunkFrame("ses-test", report));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");

        await WaitForRequestState(store, "req-task-progress", "completed");
        Assert.Equal(report, store.GetRequest("req-task-progress")!.OutcomeJson);
        foreach (var item in store.Replay(store.WorkerGeneration, 0).Events)
            Assert.DoesNotContain("Working on", item.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("overflow")]
    [InlineData("fenced")]
    [InlineData("bad-denial")]
    public async Task TaskSubmitFailsClosedWhenReportIsInvalid(string scenario)
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using var store = new WorkerStore(options);
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store);
        runtime.Start();
        var lease = store.AcquireLease("controller-test", Nonce(3));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
        using var envelope = JsonDocument.Parse("""{"method":"session/prompt","params":{"sessionId":"ses-test","prompt":[{"type":"text","text":"bounded task"}]}}""");
        await runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req-task-invalid", envelope.RootElement, "turn-task-invalid", CancellationToken.None, captureTaskReport: true);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        var valid = """{"summary":"done","changedPaths":[],"tests":[],"deniedAction":null,"limitations":[]}""";
        var text = scenario switch
        {
            "malformed" => "{not-json",
            "overflow" => new string('x', WorkerProtocol.MaxModelTaskReportBytes + 1),
            "fenced" => "```json\n" + valid + "\n```",
            "bad-denial" => """{"summary":"done","changedPaths":[],"tests":[],"deniedAction":{"requested":"secret","action":"read","result":"denied","noSideEffect":false},"limitations":[]}""",
            _ => throw new InvalidOperationException(),
        };
        input.Enqueue(ChunkFrame("ses-test", text));
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"end_turn\"}}}}\n");

        await WaitForRequestState(store, "req-task-invalid", "failed");
        var failed = store.GetRequest("req-task-invalid")!;
        Assert.Equal("{\"category\":\"model-report-invalid\"}", failed.OutcomeJson);
        Assert.DoesNotContain(text[..Math.Min(text.Length, 32)], failed.OutcomeJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskSubmitEmitsCanonicalCancelledOutcomeWhenForwardedCancellationStopsTheTurn()
    {
        using var temp = new WorkerTemp();
        using var store = new WorkerStore(temp.Options());
        SeedInstalledOrientation(store, "ora-1", "v1", "orientation-current.md", "# Orientation\n");
        await using var input = new GateStream();
        await using var output = new CaptureStream();
        await using var runtime = new WorkerRuntime(store, input, output, store);
        runtime.Start();
        var lease = store.AcquireLease("controller-test", Nonce(4));
        store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
        using var envelope = JsonDocument.Parse("""{"method":"session/prompt","params":{"sessionId":"ses-test","prompt":[{"type":"text","text":"bounded task"}]}}""");
        await runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req-task-cancel", envelope.RootElement, "turn-task-cancel", CancellationToken.None, captureTaskReport: true);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        using var cancellation = JsonDocument.Parse("""{"method":"session/cancel","params":{"sessionId":"ses-test"}}""");
        var forwarded = await runtime.CancelAsync(lease.Epoch, lease.ConnectionNonce, "cancel-task", "req-task-cancel", cancellation.RootElement, CancellationToken.None);
        Assert.Equal("forwarded", forwarded.State);
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"stopReason\":\"cancelled\"}}}}\n");

        await WaitForRequestState(store, "req-task-cancel", "failed");
        Assert.Equal("{\"category\":\"cancelled\"}", store.GetRequest("req-task-cancel")!.OutcomeJson);
    }

    [Fact]
    public void StoreMigratesExactSchemaV10AndAddsUsableOrientationComprehension()
    {
        using var temp = new WorkerTemp();
        var options = temp.Options();
        using (var store = new WorkerStore(options)) { }
        var db = System.IO.Path.Combine(temp.Path, "bridge.db");
        using (var connection = Open(db))
        {
            connection.Execute("DROP TABLE orientation_comprehension; UPDATE meta SET value='10' WHERE key='schema_version'; UPDATE meta SET value='hvo-worker-bridge-v10-20260919' WHERE key='schema_signature'; PRAGMA wal_checkpoint(TRUNCATE)");
        }

        using (var migrated = new WorkerStore(options))
        {
            using var connection = Open(db);
            Assert.Equal("11", Scalar(connection, "SELECT value FROM meta WHERE key='schema_version'"));
            Assert.Equal("hvo-worker-bridge-v11-20260919", Scalar(connection, "SELECT value FROM meta WHERE key='schema_signature'"));
            var lease = migrated.AcquireLease("controller-test", Nonce(1));
            migrated.BeginOrientationComprehension(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1");
            var canonical = "{\"assignmentId\":\"ora-1\"}";
            var hash = WorkerProtocol.OrientationContentHash(Encoding.UTF8.GetBytes(canonical));
            migrated.CompleteOrientationComprehension(lease.Epoch, lease.ConnectionNonce, "ora-1", "emp-1", "ses-test", "v1", hash, canonical);
            Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM orientation_comprehension")));
            Assert.Equal("comprehended", migrated.Status().OrientationComprehensionState);
        }
    }

    private static async Task WaitForRequestState(WorkerStore store, string requestId, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (store.GetRequest(requestId)?.State == expected) return;
            await Task.Delay(10, timeout.Token);
        }
        throw new Xunit.Sdk.XunitException($"Request {requestId} did not reach {expected}.");
    }

    private static void SeedInstalledOrientation(WorkerStore store, string assignmentId, string version, string fileName, string content)
    {
        store.BeginProcessStart();
        store.CompleteProcessStart("lifecycle", 123);
        store.SetAcpInitialized(true);
        store.BindSession("ses-test");
        var lease = store.AcquireLease("controller-test", Nonce(1));
        var hash = Hash(content);
        store.BeginOrientationInstall(lease.Epoch, lease.ConnectionNonce, assignmentId, version, fileName, hash);
        store.CompleteOrientationInstall(lease.Epoch, lease.ConnectionNonce, assignmentId, version, hash, WorkerProtocol.OrientationRootDirectory + "/" + fileName);
    }

    private static string EvidenceJson(string assignmentId, string employeeId, string sessionId, string version) => JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["assignmentId"] = assignmentId,
        ["employeeId"] = employeeId,
        ["sessionId"] = sessionId,
        ["orientationVersion"] = version,
        ["identity"] = "Operations / IT",
        ["department"] = "Operations",
        ["reporting"] = "owner",
        ["duties"] = new[] { "operate the control host" },
        ["restrictions"] = new[] { "no secrets" },
        ["escalation"] = "escalate uncertainty",
    });

    private static string ChunkFrame(string sessionId, string text) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } } } }) + "\n";

    private static JsonDocument ComprehensionMessage(long epoch, string nonce, string assignmentId, string employeeId, string sessionId, string version) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = "orientation-comprehension",
            ["epoch"] = epoch,
            ["connectionNonce"] = nonce,
            ["assignmentId"] = assignmentId,
            ["employeeId"] = employeeId,
            ["sessionId"] = sessionId,
            ["orientationVersion"] = version,
        }));

    private sealed class GateStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _current; private int _offset;
        public void Enqueue(string value) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(value));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                try { _current = await _channel.Reader.ReadAsync(cancellationToken); } catch (System.Threading.Channels.ChannelClosedException) { return 0; }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CaptureStream : MemoryStream
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text => Encoding.UTF8.GetString(ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.WriteAsync(buffer, cancellationToken);
            if (buffer.Span.Contains((byte)'\n')) Written.TrySetResult();
            return result;
        }
    }

    private static async Task AssertRejected(WorkerBridge bridge, Lease lease, JsonDocument message)
    {
        await Assert.ThrowsAsync<WorkerProtocolException>(() => bridge.DispatchAsync(new MemoryStream(), message.RootElement, lease, CancellationToken.None));
    }

    private static JsonDocument InstallMessage(long epoch, string nonce, string assignmentId, string version, string fileName, string content, string? hash = null, string? extra = null)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = "install-orientation",
            ["epoch"] = epoch,
            ["connectionNonce"] = nonce,
            ["assignmentId"] = assignmentId,
            ["orientationVersion"] = version,
            ["artifactFileName"] = fileName,
            ["contentHash"] = hash ?? Hash(content),
            ["content"] = content,
        };
        if (extra is not null) fields[extra] = "unexpected";
        return JsonDocument.Parse(JsonSerializer.Serialize(fields));
    }

    private static OrientationInstallRecord ReadResult(MemoryStream response)
    {
        using var document = JsonDocument.Parse(response.ToArray());
        return JsonSerializer.Deserialize<OrientationInstallRecord>(document.RootElement.GetProperty("result").GetRawText(), WorkerProtocol.JsonOptions)!;
    }

    private static string Hash(string content) => WorkerProtocol.OrientationContentHash(Encoding.UTF8.GetBytes(content));
    private static string Nonce(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"); connection.Open(); return connection; }
    private static object? Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }

    private sealed class RecordingInstaller : IWorkerOrientationInstaller
    {
        public List<(string AssignmentId, string Version, string FileName, string Hash)> Calls { get; } = [];
        public List<(string AssignmentId, string Version, string FileName, string Hash)> Reads { get; } = [];
        public bool Fail { get; set; }
        public bool FailRead { get; set; }
        public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("# Orientation\n");

        public Task<OrientationInstallReceipt> InstallAsync(string assignmentId, string orientationVersion, string artifactFileName, byte[] content, string contentHash, CancellationToken cancellationToken)
        {
            Calls.Add((assignmentId, orientationVersion, artifactFileName, contentHash));
            if (Fail) return Task.FromException<OrientationInstallReceipt>(new IOException("injected orientation write failure"));
            return Task.FromResult(new OrientationInstallReceipt(WorkerProtocol.OrientationRootDirectory + "/" + artifactFileName, contentHash));
        }

        public Task<OrientationReadReceipt> ReadAsync(string assignmentId, string orientationVersion, string artifactFileName, string contentHash, CancellationToken cancellationToken)
        {
            Reads.Add((assignmentId, orientationVersion, artifactFileName, contentHash));
            if (FailRead) return Task.FromException<OrientationReadReceipt>(new IOException("injected orientation read failure"));
            return Task.FromResult(new OrientationReadReceipt(WorkerProtocol.OrientationRootDirectory + "/" + artifactFileName, contentHash, Content));
        }
    }

    private sealed class WorkerTemp : IDisposable
    {
        public WorkerTemp()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hvo-orientation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, (UnixFileMode)0x1C0);
        }

        public string Path { get; }

        public WorkerOptions Options() => new(Path, "worker-test", "controller-test", System.IO.Path.Combine(Path, "bridge.sock"), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
