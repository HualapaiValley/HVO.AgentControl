using HVO.AgentControl.Worker;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerBridgeTests
{
    [Fact]
    public void BootstrapCreatesPrivateKeyAndDuplicateIsVerified()
    {
        using var temp = new WorkerTemp();
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        Assert.Equal(WorkerKeyBootstrap.Bootstrap(temp.Path, key), WorkerKeyBootstrap.Bootstrap(temp.Path, key));
        var path = System.IO.Path.Combine(temp.Path, WorkerKeyBootstrap.KeyFileName);
        Assert.Equal(32, File.ReadAllBytes(path).Length);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void BootstrapRejectsMismatchHardlinkAndInvalidKey()
    {
        using var temp = new WorkerTemp(); var key = Convert.ToBase64String(new byte[32]); WorkerKeyBootstrap.Bootstrap(temp.Path, key);
        Assert.Throws<WorkerProtocolException>(() => WorkerKeyBootstrap.Bootstrap(temp.Path, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.ParseKey("AA=="));
        if (OperatingSystem.IsLinux()) { var path = System.IO.Path.Combine(temp.Path, WorkerKeyBootstrap.KeyFileName); Assert.Equal(0, link(path, System.IO.Path.Combine(temp.Path, "hard"))); Assert.ThrowsAny<Exception>(() => WorkerKeyBootstrap.ReadKey(temp.Path)); }
    }

    [Fact]
    public void CanonicalHashIsRecursiveOrderIndependentAndValueSensitive()
    {
        using var left = JsonDocument.Parse("{\"z\":[{\"b\":2,\"a\":1}],\"n\":1.00,\"s\":\"x\"}");
        using var right = JsonDocument.Parse("{\"s\":\"x\",\"n\":1,\"z\":[{\"a\":1,\"b\":2}]}");
        using var changed = JsonDocument.Parse("{\"s\":\"x\",\"n\":2,\"z\":[{\"a\":1,\"b\":2}]}");
        Assert.Equal(WorkerProtocol.CanonicalPayloadHash(left.RootElement), WorkerProtocol.CanonicalPayloadHash(right.RootElement));
        Assert.NotEqual(WorkerProtocol.CanonicalPayloadHash(left.RootElement), WorkerProtocol.CanonicalPayloadHash(changed.RootElement));
        var nested = new string('[', WorkerProtocol.MaxJsonDepth + 2) + "0" + new string(']', WorkerProtocol.MaxJsonDepth + 2);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(nested, new JsonDocumentOptions { MaxDepth = WorkerProtocol.MaxJsonDepth }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void NonceMustBeExactCanonicalBase64Of32Bytes(string value) => Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.ParseNonce(value, "nonce"));

    [Fact]
    public void HmacBindsLabelsAndZeroLengthOrMalformedProofsFail()
    {
        var key = new byte[32]; var nonce = Convert.ToBase64String(new byte[32]);
        var client = WorkerProtocol.ComputeMac(key, "client-proof", "controller", "controller-a", "worker-a", "sha256:key", nonce, nonce, 1);
        var server = WorkerProtocol.ComputeMac(key, "server-proof", "controller", "controller-a", "worker-a", "sha256:key", nonce, nonce, 1);
        Assert.False(WorkerProtocol.VerifyMac(client, server)); Assert.False(WorkerProtocol.VerifyMac(client, "not-base64")); Assert.False(WorkerProtocol.VerifyMac(client, ""));
    }

    [Fact]
    public void AuthenticationProofRejectsFutureAndExpiredChallengeTimes()
    {
        var lifetime = TimeSpan.FromSeconds(10);
        Assert.False(WorkerBridge.IsProofFresh(999, 1_000, lifetime));
        Assert.True(WorkerBridge.IsProofFresh(1_000, 1_000, lifetime));
        Assert.True(WorkerBridge.IsProofFresh(11_000, 1_000, lifetime));
        Assert.False(WorkerBridge.IsProofFresh(11_001, 1_000, lifetime));
    }

    [Fact]
    public async Task FrameReaderHandlesPartialAndRejectsMalformedOversizedAndPartialEof()
    {
        await using var partial = new ChunkedStream("{\"a\":1}\n"u8.ToArray(), 1); using var frame = await WorkerProtocol.ReadFrameAsync(partial, default); Assert.Equal(1, frame!.RootElement.GetProperty("a").GetInt32());
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadFrameAsync(new MemoryStream("{"u8.ToArray()), default));
        await Assert.ThrowsAsync<WorkerProtocolException>(async () => await WorkerProtocol.ReadFrameAsync(new MemoryStream(Enumerable.Repeat((byte)'x', WorkerProtocol.MaxControlFrameBytes + 2).Append((byte)'\n').ToArray()), default));
    }

    [Fact]
    public void StoreValidatesExactSchemaIntegrityModesAndSingleInstance()
    {
        using var temp = new WorkerTemp(); var options = temp.Options();
        using (var store = new WorkerStore(options))
        {
            Assert.Throws<WorkerStoreException>(() => new WorkerStore(options));
            var db = System.IO.Path.Combine(temp.Path, "bridge.db");
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(db));
            using var connection = Open(db); Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode")?.ToString()?.ToLowerInvariant()); Assert.Equal(2L, Convert.ToInt64(Scalar(connection, "PRAGMA synchronous"))); Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys")));
        }
        var path = System.IO.Path.Combine(temp.Path, "bridge.db"); using (var connection = Open(path)) connection.Execute("CREATE TABLE attacker(value TEXT)");
        Assert.Throws<WorkerStoreException>(() => new WorkerStore(options));
    }

    [Fact]
    public void PersistentInstanceLockOnlyRejectsALiveHolder()
    {
        using var temp = new WorkerTemp(); var options = temp.Options();
        using (var first = new WorkerStore(options)) Assert.Throws<WorkerStoreException>(() => new WorkerStore(options));
        var lockPath = System.IO.Path.Combine(temp.Path, "bridge.lock");
        Assert.True(File.Exists(lockPath));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lockPath));
        using var reopened = new WorkerStore(options);
    }

    [Fact]
    public async Task KilledSubprocessReleasesPersistentInstanceLock()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new WorkerTemp();
        var assembly = typeof(WorkerStore).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add("--worker-test-hold-store"); start.ArgumentList.Add(temp.Path);
        using var process = Process.Start(start)!;
        Assert.Equal("locked", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reopened = new WorkerStore(temp.Options());
    }

    [Fact]
    public void PersistentInstanceLockRejectsSymlinkHardlinkAndWrongMode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var target = new WorkerTemp();
        var targetPath = System.IO.Path.Combine(target.Path, "target"); File.WriteAllText(targetPath, string.Empty); File.SetUnixFileMode(targetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(System.IO.Path.Combine(target.Path, "bridge.lock"), targetPath);
        Assert.Throws<WorkerStoreException>(() => new WorkerStore(target.Options()));

        using var linked = new WorkerTemp();
        var linkedPath = System.IO.Path.Combine(linked.Path, "bridge.lock"); File.WriteAllText(linkedPath, string.Empty); File.SetUnixFileMode(linkedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, link(linkedPath, System.IO.Path.Combine(linked.Path, "hard")));
        Assert.Throws<WorkerStoreException>(() => new WorkerStore(linked.Options()));

        using var mode = new WorkerTemp();
        var modePath = System.IO.Path.Combine(mode.Path, "bridge.lock"); File.WriteAllText(modePath, string.Empty); File.SetUnixFileMode(modePath, UnixFileMode.UserRead);
        Assert.Throws<WorkerStoreException>(() => new WorkerStore(mode.Options()));
    }

    [Fact]
    public void StoreRejectsChangedSchemaCorruptionHardlinksAndModes()
    {
        using var temp = new WorkerTemp(); var options = temp.Options(); using (var store = new WorkerStore(options)) { }
        var db = System.IO.Path.Combine(temp.Path, "bridge.db");
        using (var connection = Open(db)) connection.Execute("UPDATE meta SET value='wrong' WHERE key='schema_signature'");
        Assert.Throws<WorkerStoreException>(() => new WorkerStore(options));
        File.WriteAllBytes(db, "broken"u8.ToArray()); Assert.ThrowsAny<Exception>(() => new WorkerStore(options));
        using var second = new WorkerTemp(); using (var store = new WorkerStore(second.Options())) { }
        var secondDb = System.IO.Path.Combine(second.Path, "bridge.db");
        if (OperatingSystem.IsLinux()) { Assert.Equal(0, link(secondDb, System.IO.Path.Combine(second.Path, "hard"))); Assert.Throws<WorkerStoreException>(() => new WorkerStore(second.Options())); }
        using var third = new WorkerTemp(); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(third.Path, (UnixFileMode)0x1E0); Assert.Throws<WorkerStoreException>(() => new WorkerStore(third.Options())); }
    }

    [Fact]
    public void StoreRestartInterruptsPriorProcessAndFreshStartAcknowledgmentClearsHold()
    {
        using var temp = new WorkerTemp(); var options = temp.Options();
        using (var first = new WorkerStore(options))
        {
            Start(first); first.SetActiveRequest("prior");
        }
        using var second = new WorkerStore(options);
        var interrupted = second.Status();
        Assert.Equal("exited", interrupted.ProcessState); Assert.Null(interrupted.ActiveRequestId); Assert.Equal("process-exited", interrupted.HoldReason);
        var generation = second.BeginProcessStart();
        Assert.Equal("starting", second.Status().ProcessState); Assert.Equal("process-exited", second.Status().HoldReason);
        second.CompleteProcessStart("fresh", 456);
        Assert.Equal(generation, second.ProcessGeneration); Assert.Equal("running", second.Status().ProcessState); Assert.False(second.Status().DispatchHeld);
    }

    [Fact]
    public void DispatchGateRejectsHoldExpiryAndReplayGapBeforeRequestPersistence()
    {
        using var temp = new WorkerTemp(); var clock = new FakeClock(); using var store = new WorkerStore(temp.Options(lease: TimeSpan.FromSeconds(2)), clock); Start(store);
        var lease = store.AcquireLease("controller-test", Nonce(1)); using var payload = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}");
        store.SetHold(true, "manual"); Assert.Throws<WorkerProtocolException>(() => store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "held", payload.RootElement, "turn"));
        store.SetHold(false, null); clock.Advance(TimeSpan.FromSeconds(3)); Assert.Throws<WorkerProtocolException>(() => store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "expired", payload.RootElement, "turn"));
        var fresh = store.AcquireLease("controller-test", Nonce(2)); store.SetHold(false, null); Assert.Throws<WorkerProtocolException>(() => store.Replay(store.WorkerGeneration, 1));
        Assert.Throws<WorkerProtocolException>(() => store.RegisterGatedRequest(fresh.Epoch, fresh.ConnectionNonce, "gap", payload.RootElement, "turn"));
        Assert.Equal(0L, CountRows(System.IO.Path.Combine(temp.Path, "bridge.db"), "requests"));
    }

    [Fact]
    public void SemanticRequestIdempotencyDoesNotDuplicateAndChangedValueRejects()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        using var firstJson = JsonDocument.Parse("{\"method\":\"x\",\"params\":{\"b\":2,\"a\":1}}"); using var sameJson = JsonDocument.Parse("{\"params\":{\"a\":1,\"b\":2},\"method\":\"x\"}"); using var changed = JsonDocument.Parse("{\"method\":\"x\",\"params\":{\"a\":2,\"b\":2}}");
        var first = store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "req", firstJson.RootElement, "turn"); var same = store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "req", sameJson.RootElement, "turn");
        Assert.Equal(first.PayloadHash, same.PayloadHash); Assert.Throws<WorkerProtocolException>(() => store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "req", changed.RootElement, "turn")); Assert.Equal(1L, CountRows(System.IO.Path.Combine(temp.Path, "bridge.db"), "requests"));
    }

    [Fact]
    public void ReplayBoundsNeverGrowOnOverflowAndAckAllowsPruneAndAppend()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options(eventLimit: 2, eventBytes: 4));
        var one = store.AppendEvent("one", "{}"); var two = store.AppendEvent("two", "{}");
        Assert.Throws<WorkerProtocolException>(() => store.AppendEvent("three", "{}")); Assert.Throws<WorkerProtocolException>(() => store.AppendEvent("four", "{}"));
        Assert.Equal(2, store.Replay(one.WorkerGeneration, 0).Count); Assert.True(store.Status().DispatchHeld);
        store.Acknowledge(one.WorkerGeneration, one.Sequence); var three = store.AppendEvent("three", "{}");
        Assert.Equal([two.Sequence, three.Sequence], store.Replay(three.WorkerGeneration, one.Sequence).Select(x => x.Sequence));
        Assert.Throws<WorkerProtocolException>(() => store.Replay(three.WorkerGeneration, three.Sequence + 1));
        Assert.Throws<WorkerProtocolException>(() => store.Replay(three.WorkerGeneration + 1, 0));
    }

    [Fact]
    public void PriorGenerationEventsSurviveRestartReplayAndLexicographicAcknowledgment()
    {
        using var temp = new WorkerTemp(); var options = temp.Options(eventLimit: 3, eventBytes: 20);
        WorkerEvent one;
        using (var first = new WorkerStore(options))
        {
            one = first.AppendEvent("one", "{}");
            first.AppendEvent("two", "{}");
        }

        using var second = new WorkerStore(options);
        Assert.Equal(one.WorkerGeneration + 1, second.WorkerGeneration);
        Assert.Equal([1L, 2L], second.Replay(one.WorkerGeneration, 0).Select(x => x.Sequence));
        Assert.Empty(second.Replay(second.WorkerGeneration, 0));
        Assert.Equal(0, second.Status().AcknowledgedWorkerGeneration);
        var current = second.AppendEvent("current", "{}");
        Assert.Throws<WorkerProtocolException>(() => second.AppendEvent("overflow", "{}"));
        second.Acknowledge(one.WorkerGeneration, 2);
        Assert.Equal(one.WorkerGeneration, second.Status().AcknowledgedWorkerGeneration);
        Assert.Equal(2, second.Status().AcknowledgedSequence);
        var appended = second.AppendEvent("after-ack", "{}");
        Assert.Equal([current.Sequence, appended.Sequence], second.Replay(current.WorkerGeneration, 0).Select(x => x.Sequence));
        Assert.Throws<WorkerProtocolException>(() => second.Acknowledge(one.WorkerGeneration, 1));
        Assert.Throws<WorkerProtocolException>(() => second.Replay(one.WorkerGeneration, 0));
        Assert.True(second.Status().DispatchHeld);
    }

    [Fact]
    public void ReplayRejectsSingleOversizedEventWithoutInsertion()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options(eventBytes: 3)); Assert.Throws<WorkerProtocolException>(() => store.AppendEvent("large", "1234")); Assert.Equal(0L, CountRows(System.IO.Path.Combine(temp.Path, "bridge.db"), "events"));
    }

    [Fact]
    public void PermissionRequiresOfferedOptionAndExactTupleAndSanitizesPayload()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); var generation = store.BeginProcessStart(); store.CompleteProcessStart("handle", 1);
        using var frame = JsonDocument.Parse("{\"requestId\":\"req\",\"turnId\":\"turn\",\"decisionId\":\"decision\",\"secret\":\"do-not-expose\",\"options\":[{\"optionId\":\"allow_once\"},{\"optionId\":\"reject_once\"}]}");
        var pending = store.AddPermission("req", "turn", "decision", frame.RootElement); Assert.DoesNotContain("do-not-expose", JsonSerializer.Serialize(store.Status().PendingPermission));
        Assert.Throws<WorkerProtocolException>(() => store.BeginPermissionDecision("decision", generation, "req", "turn", "arbitrary"));
        var deciding = store.BeginPermissionDecision("decision", generation, "req", "turn", "reject_once"); Assert.Equal("deciding", deciding.State); store.MarkPermissionUncertain("decision");
        Assert.Throws<WorkerProtocolException>(() => store.BeginPermissionDecision("decision", generation, "req", "turn", "reject_once"));
        Assert.Equal("uncertain", store.Status().PendingPermission!.State);
        Assert.Contains("allow_once", pending.OptionIds);
    }

    [Fact]
    public void PermissionInvalidatesOnProcessGenerationChange()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); var generation = store.BeginProcessStart(); store.CompleteProcessStart("one", 1);
        using var frame = JsonDocument.Parse("{\"options\":[{\"optionId\":\"reject_once\"}]}"); store.AddPermission("req", "turn", "decision", frame.RootElement); store.SetProcess("stopped"); store.BeginProcessStart();
        Assert.Null(store.Status().PendingPermission); Assert.Throws<WorkerProtocolException>(() => store.BeginPermissionDecision("decision", generation, "req", "turn", "reject_once"));
    }

    [Fact]
    public async Task PinnedPermissionFrameBindsOnlyToHostOwnedActivePromptAndIgnoresSpoofedIds()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"ses-current\",\"text\":\"permission\"}}");
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "host-request", envelope.RootElement, "host-turn", CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        input.Enqueue("""{"jsonrpc":"2.0","id":9001,"method":"session/request_permission","params":{"sessionId":"ses-current","requestId":"spoofed-request","turnId":"spoofed-turn","decisionId":"spoofed-decision","toolCall":{"toolCallId":"tc-1","kind":"read","title":"diagnostic:public","status":"pending","rawInput":{"secret":"do-not-retain"}},"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}}""" + "\n");
        await Eventually(() => store.Status().PendingPermission is not null);
        var pending = store.Status().PendingPermission!;
        Assert.Equal("host-request", pending.RequestId); Assert.Equal("host-turn", pending.TurnId); Assert.StartsWith("perm:", pending.DecisionId, StringComparison.Ordinal);
        Assert.NotEqual("spoofed-decision", pending.DecisionId); Assert.Contains("reject_once", pending.OptionIds); Assert.DoesNotContain("do-not-retain", JsonSerializer.Serialize(pending), StringComparison.Ordinal);
        var acpId = JsonDocument.Parse(output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.GetProperty("id").GetInt64();
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"ok\":true}}}}\n");
        Assert.Equal("completed", (await submit.WaitAsync(TimeSpan.FromSeconds(2))).State);
    }

    [Fact]
    public async Task PermissionWithoutExactPromptOrWithWrongKnownSessionFailsClosedWithoutStoppingReader()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        input.Enqueue("""{"jsonrpc":"2.0","id":8001,"method":"session/request_permission","params":{"options":[{"optionId":"reject_once","kind":"reject_once"}]}}""" + "\n");
        await Eventually(() => output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1);
        Assert.Null(store.Status().PendingPermission); Assert.Equal("running", store.Status().ProcessState);
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"ses-current\"}}");
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", CancellationToken.None);
        await Eventually(() => output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2);
        input.Enqueue("""{"jsonrpc":"2.0","id":8002,"method":"session/request_permission","params":{"sessionId":"ses-other","options":[{"optionId":"reject_once","kind":"reject_once"}]}}""" + "\n");
        await Eventually(() => output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3);
        Assert.Null(store.Status().PendingPermission); Assert.Equal("running", store.Status().ProcessState);
        var promptFrame = JsonDocument.Parse(output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{promptFrame.RootElement.GetProperty("id").GetInt64()},\"result\":{{}}}}\n");
        await submit.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectorDisconnectDoesNotCancelDurablePermissionDecisionWrite()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new GatedFlushStream(2); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{\"sessionId\":\"ses-current\"}}");
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", CancellationToken.None);
        await output.FirstFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));
        input.Enqueue("""{"jsonrpc":"2.0","id":9001,"method":"session/request_permission","params":{"sessionId":"ses-current","options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}}""" + "\n");
        await Eventually(() => store.Status().PendingPermission is not null); var pending = store.Status().PendingPermission!; using var disconnected = new CancellationTokenSource();
        var decision = runtime.DecidePermissionAsync(pending.DecisionId, pending.ProcessGeneration, pending.RequestId, pending.TurnId, "reject_once", disconnected.Token);
        await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); disconnected.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decision); output.Release.TrySetResult();
        await Eventually(() => store.Status().PendingPermission is null);
        Assert.Equal("decided", store.DecidePermission(pending.DecisionId, pending.ProcessGeneration, pending.RequestId, pending.TurnId, "reject_once").State);
        var promptFrame = JsonDocument.Parse(output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]); input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{promptFrame.RootElement.GetProperty("id").GetInt64()},\"result\":{{}}}}\n"); await submit.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcpEofOrReadFailureInterruptsForwardedRequestAndHoldsDispatch(bool throwOnRead)
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = throwOnRead ? new FailingGateStream() : new GateStream();
        await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{\"text\":\"transport\"}}");
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (input is FailingGateStream failing) failing.Fail(new IOException("raw injected transport detail")); else ((GateStream)input).Complete();
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => submit.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("raw injected", failure.Message, StringComparison.Ordinal);
        await Eventually(() => store.GetRequest("req")?.State == "uncertain" && store.Status().ActiveRequestId is null);
        Assert.Equal("exited", store.Status().ProcessState); Assert.Equal("process-exited", store.Status().HoldReason);
        var replayed = await runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", CancellationToken.None);
        Assert.Equal("uncertain", replayed.State);
        using var fresh = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}");
        await Assert.ThrowsAsync<WorkerProtocolException>(() => runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "new", fresh.RootElement, "turn2", CancellationToken.None));
        Assert.Equal(1, output.Text.Count(character => character == '\n'));
    }

    [Fact]
    public async Task ResponseCompletedBeforeEofRemainsCompleted()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}");
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64();
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"ok\":true}}}}\n");
        Assert.Equal("completed", (await submit.WaitAsync(TimeSpan.FromSeconds(2))).State);
        input.Complete(); await Eventually(() => store.Status().ProcessState == "exited");
        Assert.Equal("completed", store.GetRequest("req")!.State);
    }

    [Fact]
    public async Task ConnectorCancellationDoesNotOwnForwardedRequestOrPromptSlot()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var envelope = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{\"text\":\"delayed\"}}"); using var disconnected = new CancellationTokenSource();
        var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "req", envelope.RootElement, "turn", disconnected.Token);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2)); disconnected.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.Equal("req", store.Status().ActiveRequestId); Assert.Equal("forwarded", store.GetRequest("req")!.State);
        var acpId = JsonDocument.Parse(output.Text).RootElement.GetProperty("id").GetInt64(); input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{acpId},\"result\":{{\"ok\":true}}}}\n");
        await Eventually(() => store.GetRequest("req")?.State == "completed" && store.Status().ActiveRequestId is null); Assert.Equal(1L, CountRows(System.IO.Path.Combine(temp.Path, "bridge.db"), "requests"));
    }

    [Fact]
    public async Task SecondPromptIsRejectedBeforePersistenceAndNonPromptDoesNotClearActivePrompt()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new CaptureStream(); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var prompt = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}");
        var first = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "prompt-1", prompt.RootElement, "turn-1", CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "prompt-2", prompt.RootElement, "turn-2", CancellationToken.None));
        Assert.Null(store.GetRequest("prompt-2"));

        using var nonPrompt = JsonDocument.Parse("{\"method\":\"session/status\",\"params\":{}}");
        var concurrent = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "status-1", nonPrompt.RootElement, null, CancellationToken.None);
        await Eventually(() => output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2);
        Assert.Equal("prompt-1", store.Status().ActiveRequestId);
        var frames = output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => JsonDocument.Parse(value)).ToArray();
        var promptId = frames.Single(x => x.RootElement.GetProperty("method").GetString() == "session/prompt").RootElement.GetProperty("id").GetInt64();
        var statusId = frames.Single(x => x.RootElement.GetProperty("method").GetString() == "session/status").RootElement.GetProperty("id").GetInt64();
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{statusId},\"result\":{{}}}}\n");
        await concurrent.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("prompt-1", store.Status().ActiveRequestId);
        input.Enqueue($"{{\"jsonrpc\":\"2.0\",\"id\":{promptId},\"result\":{{}}}}\n");
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var frame in frames) frame.Dispose();
    }

    [Fact]
    public async Task StoreSerializesConcurrentConnectionUse()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options(eventLimit: 1000)); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        using var payload = JsonDocument.Parse("{\"method\":\"session/status\"}");
        var tasks = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            store.Heartbeat(lease.Epoch, lease.ConnectionNonce);
            var request = store.RegisterAndBeginForwardingGated(lease.Epoch, lease.ConnectionNonce, $"req-{index}", payload.RootElement, null);
            store.MarkForwarded(request.RequestId);
            store.CompleteRequest(request.RequestId, "completed", "{}");
            store.AppendEvent("stress", "{}");
            _ = store.Status();
            _ = store.GetRequest(request.RequestId);
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(Enumerable.Range(0, 100), index => Assert.Equal("completed", store.GetRequest($"req-{index}")!.State));
        Assert.Equal(100, store.Replay(store.WorkerGeneration, 0).Count);
    }

    [Fact]
    public void OldConnectionIsFencedAfterSameControllerReconnect()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); var first = store.AcquireLease("controller-test", Nonce(1)); var second = store.AcquireLease("controller-test", Nonce(2)); Assert.Throws<WorkerProtocolException>(() => store.Heartbeat(first.Epoch, first.ConnectionNonce)); store.Heartbeat(second.Epoch, second.ConnectionNonce);
    }

    [Fact]
    public async Task CapturedOldSocketLeaseRejectsStatusReplayAndReconcileBeforeSideEffects()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var first = store.AcquireLease("controller-test", Nonce(1));
        var runtime = new WorkerRuntime(store, new GateStream(), new CaptureStream());
        await using var bridge = new WorkerBridge(temp.Options(), store, runtime, new byte[32]);
        _ = store.AcquireLease("controller-test", Nonce(2));
        foreach (var json in new[]
        {
            "{\"operation\":\"status\"}",
            $"{{\"operation\":\"replay\",\"workerGeneration\":{store.WorkerGeneration + 99},\"afterSequence\":0}}",
            "{\"operation\":\"reconcile\",\"requestId\":\"unknown\"}",
        })
        {
            using var message = JsonDocument.Parse(json); await using var response = new MemoryStream();
            await Assert.ThrowsAsync<WorkerProtocolException>(() => bridge.DispatchAsync(response, message.RootElement, first, CancellationToken.None));
        }
        Assert.False(store.Status().DispatchHeld);
    }

    [Fact]
    public void CancellationIntentIsDurableIdempotentAndChangedReuseRejects()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        using var prompt = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}"); store.RegisterGatedRequest(lease.Epoch, lease.ConnectionNonce, "target", prompt.RootElement, "turn"); store.MarkForwarding("target");
        using var first = JsonDocument.Parse("{\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"one\"}}");
        using var same = JsonDocument.Parse("{\"params\":{\"sessionId\":\"one\"},\"method\":\"session/cancel\"}");
        using var changed = JsonDocument.Parse("{\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"two\"}}");
        var registered = store.RegisterCancellation(lease.Epoch, lease.ConnectionNonce, "cancel-1", "target", first.RootElement);
        Assert.Equal(registered.PayloadHash, store.RegisterCancellation(lease.Epoch, lease.ConnectionNonce, "cancel-1", "target", same.RootElement).PayloadHash);
        Assert.Throws<WorkerProtocolException>(() => store.RegisterCancellation(lease.Epoch, lease.ConnectionNonce, "cancel-1", "target", changed.RootElement));
        Assert.Equal(1L, CountRows(System.IO.Path.Combine(temp.Path, "bridge.db"), "cancellations"));
    }

    [Fact]
    public async Task FailedCancellationWriteBecomesUncertainAndIsNotBlindlyRetried()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new FailAfterNewlinesStream(1); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var prompt = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}"); var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "target", prompt.RootElement, "turn", CancellationToken.None);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = JsonDocument.Parse("{\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"ses\"}}");
        await Assert.ThrowsAsync<WorkerProtocolException>(() => runtime.CancelAsync(lease.Epoch, lease.ConnectionNonce, "cancel-failed", "target", cancellation.RootElement, CancellationToken.None));
        Assert.Equal("uncertain", store.GetCancellation("cancel-failed")!.State);
        Assert.Equal("uncertain", (await runtime.CancelAsync(lease.Epoch, lease.ConnectionNonce, "cancel-failed", "target", cancellation.RootElement, CancellationToken.None)).State);
        Assert.Equal(1, output.NewlineWrites);
        input.Complete(); await Assert.ThrowsAsync<WorkerProtocolException>(() => submit.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ConnectorDisconnectDoesNotCancelDurableCancellationWrite()
    {
        using var temp = new WorkerTemp(); using var store = new WorkerStore(temp.Options()); Start(store); var lease = store.AcquireLease("controller-test", Nonce(1));
        await using var input = new GateStream(); await using var output = new GatedFlushStream(2); await using var runtime = new WorkerRuntime(store, input, output); runtime.Start();
        using var prompt = JsonDocument.Parse("{\"method\":\"session/prompt\",\"params\":{}}"); var submit = runtime.SubmitAsync(lease.Epoch, lease.ConnectionNonce, "target", prompt.RootElement, "turn", CancellationToken.None);
        await output.FirstFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelEnvelope = JsonDocument.Parse("{\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"ses\"}}"); using var disconnected = new CancellationTokenSource();
        var cancel = runtime.CancelAsync(lease.Epoch, lease.ConnectionNonce, "cancel-1", "target", cancelEnvelope.RootElement, disconnected.Token);
        await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); disconnected.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel); output.Release.TrySetResult();
        await Eventually(() => store.GetCancellation("cancel-1")?.State == "forwarded");
        Assert.Equal(2, output.FlushCount); Assert.Equal("forwarded", store.GetCancellation("cancel-1")!.State);
        input.Complete(); await Assert.ThrowsAsync<WorkerProtocolException>(() => submit.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ControlHostWorkerFlagRemainsFalseBySourceContract()
    {
        var source = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src/HVO.AgentControl/Program.cs")); Assert.Contains("WorkerControlImplemented: false", source, StringComparison.Ordinal);
    }

    private static void Start(WorkerStore store) { store.BeginProcessStart(); store.CompleteProcessStart("lifecycle", 123); }
    private static string Nonce(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"); connection.Open(); return connection; }
    private static object? Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static long CountRows(string path, string table) { using var connection = Open(path); return Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM {table}")); }
    private static async Task Eventually(Func<bool> predicate) { var deadline = DateTime.UtcNow.AddSeconds(3); while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20); Assert.True(predicate()); }
    private static string RepoRoot() => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    [DllImport("libc", EntryPoint = "link")] private static extern int link(string oldPath, string newPath);

    private sealed class WorkerTemp : IDisposable
    {
        public WorkerTemp() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hvo-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, (UnixFileMode)0x1C0); }
        public string Path { get; }
        public WorkerOptions Options(TimeSpan? lease = null, int eventLimit = 10_000, long eventBytes = 64 * 1024 * 1024) => new(Path, "worker-test", "controller-test", System.IO.Path.Combine(Path, "bridge.sock"), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250), lease ?? TimeSpan.FromSeconds(20), eventLimit, eventBytes);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
    private sealed class FakeClock : IWorkerClock { public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch; public long MonotonicMilliseconds { get; private set; } public void Advance(TimeSpan value) { UtcNow += value; MonotonicMilliseconds += (long)value.TotalMilliseconds; } }
    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes) { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken); }
    private class GateStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(); private byte[]? _current; private int _offset;
        public void Enqueue(string value) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(value));
        public void Complete() => _channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { if (_current is null || _offset == _current.Length) { try { _current = await _channel.Reader.ReadAsync(cancellationToken); } catch (ChannelClosedException) { return 0; } _offset = 0; } var count = Math.Min(buffer.Length, _current.Length - _offset); _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FailingGateStream : GateStream
    {
        private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fail(Exception exception) => _failure.TrySetResult(exception);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw await _failure.Task.WaitAsync(cancellationToken);
    }
    private class CaptureStream : MemoryStream
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public string Text => Encoding.UTF8.GetString(ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { var result = base.WriteAsync(buffer, cancellationToken); if (buffer.Span.Contains((byte)'\n')) Written.TrySetResult(); return result; }
    }
    private sealed class FailAfterNewlinesStream(int allowedNewlines) : CaptureStream
    {
        public int NewlineWrites { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.Contains((byte)'\n'))
            {
                if (NewlineWrites >= allowedNewlines) throw new IOException("injected write failure");
                NewlineWrites++;
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
    private sealed class GatedFlushStream(int blockedFlush) : CaptureStream
    {
        public TaskCompletionSource FirstFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FlushCount { get; private set; }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            if (FlushCount == 1) FirstFlush.TrySetResult();
            if (FlushCount == blockedFlush) { Blocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await base.FlushAsync(cancellationToken);
        }
    }
}

internal static class SqliteTestExtensions { public static void Execute(this SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); } }
