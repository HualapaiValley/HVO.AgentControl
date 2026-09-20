using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The employee-scoped task HTTP contract against a real enabled runtime with a
/// usable WorkerControl configuration, a seeded managed worker and the worker
/// bridge replaced by an in-process fake. No SSH, Docker or provider is touched.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class EmployeeTaskApiRuntimeTests : IClassFixture<EmployeeTaskRuntimeFactory>
{
    private readonly EmployeeTaskRuntimeFactory _factory;
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private (string EmployeeId, string WorkerId, string NativeSessionId) _seed;

    public EmployeeTaskApiRuntimeTests(EmployeeTaskRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateResolvesIdentitiesServerSideAndIgnoresCallerSuppliedInternalIds()
    {
        var (client, seed) = await ReadyAsync();
        var submitsBefore = _factory.Fake.SubmitCount;

        // Extra binding/worker/session fields are not part of the contract and are
        // ignored; the exact server-resolved identities are used.
        var body = $$"""
        {
          "expectedEmployeeRevision": 1,
          "idempotencyKey": "http-ignore-internal",
          "runtimeBindingId": "rtb-forged",
          "workerId": "wrk-forged",
          "sessionRecordId": "acps-forged",
          "nativeSessionId": "native-forged",
          "bridgeSocketPath": "/control/bridge.sock",
          "taskSpec": {
            "description": "Add a bounded health endpoint",
            "workspaceRoot": "/workspace/project",
            "allowedPaths": ["src", "tests"],
            "allowedTools": ["read", "edit", "test"],
            "forbiddenActions": ["network egress"],
            "maximumSeconds": 300,
            "testRecipeId": "dotnet-test-release"
          }
        }
        """;
        using var response = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = detail.RootElement;
        Assert.Equal(WorkerTaskStates.Running, root.GetProperty("task").GetProperty("state").GetString());
        Assert.Equal("running", root.GetProperty("displayState").GetString());
        Assert.Equal(seed.WorkerId, root.GetProperty("task").GetProperty("workerId").GetString());
        Assert.Equal(seed.NativeSessionId, root.GetProperty("request").GetProperty("nativeSessionId").GetString());
        Assert.Equal("Add a bounded health endpoint", root.GetProperty("spec").GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("verification").ValueKind);
        Assert.Equal(submitsBefore + 1, _factory.Fake.SubmitCount);
        Assert.DoesNotContain("rtb-forged", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("wrk-forged", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("native-forged", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedIdempotencyReturnsOneTaskAndOneSubmit()
    {
        var (client, seed) = await ReadyAsync();
        var spec = Spec("http-idem-repeat");
        var first = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", spec);
        var before = _factory.Fake.SubmitCount;
        var second = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", spec);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(
            firstDocument.RootElement.GetProperty("task").GetProperty("id").GetString(),
            secondDocument.RootElement.GetProperty("task").GetProperty("id").GetString());
        Assert.Equal(before, _factory.Fake.SubmitCount);
    }

    [Fact]
    public async Task ChangedSpecificationWithSameKeyIsConflict()
    {
        var (client, seed) = await ReadyAsync();
        using var created = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-idem-conflict"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var conflict = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-idem-conflict", description: "A different change entirely"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(conflict);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task EmployeeRevisionConflictIs409()
    {
        var (client, seed) = await ReadyAsync();
        using var response = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-bad-revision", expectedRevision: 99));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await RemoteWorkerApi.ProblemAsync(response);
    }

    [Fact]
    public async Task InvalidSpecificationIs422()
    {
        var (client, seed) = await ReadyAsync();
        using var response = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-bad-spec", allowedTools: ["shell"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task UnknownEmployeeIs404AndMalformedIdIs422()
    {
        var (client, _) = await ReadyAsync();

        using var unknown = await PostAsync(client, "/api/employees/emp-does-not-exist/tasks", Spec("http-unknown"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await RemoteWorkerApi.ProblemAsync(unknown);

        using var malformed = await PostAsync(client, "/api/employees/not-an-employee/tasks", Spec("http-malformed"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        await RemoteWorkerApi.ProblemAsync(malformed);
    }

    [Fact]
    public async Task RecentListIsBoundedNewestFirstAndExposesStatesAndTimestamps()
    {
        var (client, seed) = await ReadyAsync();
        using var created = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-recent"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var createdTaskId = createdDocument.RootElement.GetProperty("task").GetProperty("id").GetString()!;

        using var recent = await client.GetAsync($"/api/employees/{seed.EmployeeId}/tasks?limit=10");
        Assert.Equal(HttpStatusCode.OK, recent.StatusCode);
        using var recentDocument = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var items = recentDocument.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        Assert.Contains(items, item => item.GetProperty("task").GetProperty("id").GetString() == createdTaskId);
        var detail = items.First(item => item.GetProperty("task").GetProperty("id").GetString() == createdTaskId);
        Assert.Equal("running", detail.GetProperty("displayState").GetString());
        Assert.NotEqual(default, detail.GetProperty("task").GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("request").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("verification").ValueKind);

        using var invalidLimit = await client.GetAsync($"/api/employees/{seed.EmployeeId}/tasks?limit=100");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidLimit.StatusCode);

        using var detailResponse = await client.GetAsync($"/api/tasks/{createdTaskId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var unknown = await client.GetAsync("/api/tasks/tsk-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var malformed = await client.GetAsync("/api/tasks/bad-id");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
    }

    [Fact]
    public async Task CancelResolvesTheCurrentRequestAndDoesNotClaimRollback()
    {
        var (client, seed) = await ReadyAsync();
        using var created = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-cancel"));
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var taskId = createdDocument.RootElement.GetProperty("task").GetProperty("id").GetString()!;
        var revision = createdDocument.RootElement.GetProperty("task").GetProperty("revision").GetInt32();

        using var response = await PostAsync(client, $"/api/tasks/{taskId}/cancel", $$"""{"expectedTaskRevision":{{revision}}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Forwarded", root.GetProperty("cancellation").GetProperty("state").GetString());
        Assert.Equal(taskId, root.GetProperty("task").GetProperty("task").GetProperty("id").GetString());
        // A forwarded cancellation is not a rollback and does not move the task to Cancelled.
        Assert.Equal(WorkerTaskStates.Running, root.GetProperty("task").GetProperty("task").GetProperty("state").GetString());

        using var stale = await PostAsync(client, $"/api/tasks/{taskId}/cancel", """{"expectedTaskRevision":1}""");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task SyncIsRevisionBoundNeverResubmitsAndGetShowsReport()
    {
        var (client, seed) = await ReadyAsync();
        using var created = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-sync"));
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var task = createdDocument.RootElement.GetProperty("task");
        var taskId = task.GetProperty("id").GetString()!;
        var revision = task.GetProperty("revision").GetInt32();
        var requestId = createdDocument.RootElement.GetProperty("request").GetProperty("id").GetString()!;
        var submits = _factory.Fake.SubmitCount;
        _factory.Fake.Complete(requestId);

        using var synchronized = await PostAsync(client, $"/api/tasks/{taskId}/sync", $$"""{"expectedTaskRevision":{{revision}}}""");
        Assert.Equal(HttpStatusCode.OK, synchronized.StatusCode);
        using var synchronizedDocument = JsonDocument.Parse(await synchronized.Content.ReadAsStringAsync());
        Assert.Equal("remote-completed", synchronizedDocument.RootElement.GetProperty("detail").GetString());
        Assert.Equal(WorkerTaskStates.Completed, synchronizedDocument.RootElement.GetProperty("task").GetProperty("task").GetProperty("state").GetString());
        Assert.Equal(submits, _factory.Fake.SubmitCount);

        using var get = await client.GetAsync($"/api/tasks/{taskId}");
        using var getDocument = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, getDocument.RootElement.GetProperty("task").GetProperty("modelReportJson").ValueKind);
        using var stale = await PostAsync(client, $"/api/tasks/{taskId}/sync", $$"""{"expectedTaskRevision":{{revision}}}""");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task EmployeeDispatchHoldIsRevisionAndOriginBound()
    {
        var (client, seed) = await ReadyAsync();
        using var held = await PutAsync(client, $"/api/employees/{seed.EmployeeId}/dispatch-hold", """{"expectedEmployeeRevision":1,"held":true,"detail":"maintenance"}""");
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        using var blocked = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-held"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var cleared = await PutAsync(client, $"/api/employees/{seed.EmployeeId}/dispatch-hold", """{"expectedEmployeeRevision":1,"held":false,"detail":null}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var staleRevision = await PutAsync(client, $"/api/employees/{seed.EmployeeId}/dispatch-hold", """{"expectedEmployeeRevision":99,"held":true}""");
        Assert.Equal(HttpStatusCode.Conflict, staleRevision.StatusCode);
    }

    [Fact]
    public async Task ReadsAndMutationsRequireOwnerAuthAndSameOrigin()
    {
        await ReadyAsync();
        using var anonymous = _factory.CreateClient();
        using var read = await anonymous.GetAsync($"/api/employees/{_seed.EmployeeId}/tasks");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);

        var (client, seed) = await ReadyAsync();
        // A cross-origin create is rejected before any dispatch.
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, $"/api/employees/{seed.EmployeeId}/tasks")
        {
            Content = new StringContent(Spec("http-cross-origin"), Encoding.UTF8, "application/json"),
        };
        crossOrigin.Headers.Add("Origin", "https://other.example");
        crossOrigin.Headers.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        var submitsBefore = _factory.Fake.SubmitCount;
        using var rejected = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(submitsBefore, _factory.Fake.SubmitCount);

        using var cancelCrossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/tsk-does-not-exist/cancel")
        {
            Content = new StringContent("""{"expectedTaskRevision":1}""", Encoding.UTF8, "application/json"),
        };
        cancelCrossOrigin.Headers.Add("Origin", "https://other.example");
        cancelCrossOrigin.Headers.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        using var cancelRejected = await client.SendAsync(cancelCrossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, cancelRejected.StatusCode);

        using var syncCrossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/tsk-does-not-exist/sync")
        {
            Content = new StringContent("""{"expectedTaskRevision":1}""", Encoding.UTF8, "application/json"),
        };
        syncCrossOrigin.Headers.Add("Origin", "https://other.example");
        syncCrossOrigin.Headers.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        using var syncRejected = await client.SendAsync(syncCrossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, syncRejected.StatusCode);

        using var holdCrossOrigin = new HttpRequestMessage(HttpMethod.Put, $"/api/employees/{seed.EmployeeId}/dispatch-hold")
        {
            Content = new StringContent("""{"expectedEmployeeRevision":1,"held":true}""", Encoding.UTF8, "application/json"),
        };
        holdCrossOrigin.Headers.Add("Origin", "https://other.example");
        holdCrossOrigin.Headers.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        using var holdRejected = await client.SendAsync(holdCrossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, holdRejected.StatusCode);
    }

    [Fact]
    public async Task PortalEmployeeDetailCarriesAdditiveRecentTasksForTheManagedEmployee()
    {
        var (client, seed) = await ReadyAsync();
        using var created = await PostAsync(client, $"/api/employees/{seed.EmployeeId}/tasks", Spec("http-portal"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var taskId = createdDocument.RootElement.GetProperty("task").GetProperty("id").GetString();

        using var response = await client.GetAsync($"/api/employees/{seed.EmployeeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recentTasks = document.RootElement.GetProperty("recentTasks");
        Assert.Equal(JsonValueKind.Array, recentTasks.ValueKind);
        Assert.Contains(recentTasks.EnumerateArray(), item => item.GetProperty("task").GetProperty("id").GetString() == taskId);
    }

    [Fact]
    public async Task NonManagedSeedEmployeeHasAnEmptyRecentTasksArray()
    {
        using var factory = new EnabledRuntimeFactory();
        using var client = factory.CreateClient();
        await factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);

        using var overview = await client.GetAsync("/api/organization/portal");
        using var overviewDocument = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        var employeeId = Assert.Single(overviewDocument.RootElement.GetProperty("employees").EnumerateArray()).GetProperty("id").GetString()!;

        using var response = await client.GetAsync($"/api/employees/{employeeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("recentTasks").EnumerateArray());
    }

    private async Task<(HttpClient Client, (string EmployeeId, string WorkerId, string NativeSessionId) Seed)> ReadyAsync()
    {
        await _seedGate.WaitAsync();
        try
        {
            var client = _factory.CreateClient();
            await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(60));
            client.DefaultRequestHeaders.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
            if (_seed.EmployeeId is null) _seed = _factory.SeedManagedWorker();
            return (client, _seed);
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string body) => SendAsync(client, HttpMethod.Post, path, body);
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string path, string body) => SendAsync(client, HttpMethod.Put, path, body);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }

    private static string Spec(
        string idempotencyKey,
        string description = "Add a bounded health endpoint",
        IReadOnlyList<string>? allowedTools = null,
        int expectedRevision = 1) =>
        JsonSerializer.Serialize(new
        {
            expectedEmployeeRevision = expectedRevision,
            idempotencyKey,
            taskSpec = new
            {
                description,
                workspaceRoot = "/workspace/project",
                allowedPaths = new[] { "src", "tests" },
                allowedTools = allowedTools ?? ["read", "edit", "test"],
                forbiddenActions = new[] { "network egress" },
                maximumSeconds = 300,
                testRecipeId = "dotnet-test-release",
            },
        });
}

/// <summary>
/// An enabled runtime with a usable WorkerControl configuration, a seeded managed
/// DeveloperContainer worker and the bridge session factory replaced by an
/// in-process fake.
/// </summary>
public sealed class EmployeeTaskRuntimeFactory : EnabledRuntimeFactory
{
    private readonly string _workerRoot;

    public EmployeeTaskRuntimeFactory()
        : base("prompt_fast", workerControlEnabled: true)
    {
        _workerRoot = Path.Combine(Path.GetTempPath(), "agentcontrol-task-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workerRoot);
        KnownHostsPath = Path.Combine(_workerRoot, "known_hosts");
        IdentityFilePath = Path.Combine(_workerRoot, "id");
        File.WriteAllText(KnownHostsPath, "task.example ssh-ed25519 AAAA\n");
        File.WriteAllText(IdentityFilePath, "not-a-real-key");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KnownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(IdentityFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string KnownHostsPath { get; }
    public string IdentityFilePath { get; }
    public FakeTaskBridgeSession Fake { get; } = new();

    private readonly object _seedLock = new();
    private (string EmployeeId, string WorkerId, string NativeSessionId)? _seed;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("WorkerControl:Enabled", "true");
        builder.UseSetting("WorkerControl:ControllerId", "controller-task");
        builder.UseSetting("WorkerControl:ApprovedImageDigest", "sha256:" + new string('4', 64));
        builder.UseSetting("WorkerControl:ApprovedImagePlatform", "linux/amd64");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Id", "host-task");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Hostname", "task.example");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Port", "22");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Username", "docker");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:KnownHostsPath", KnownHostsPath);
        builder.UseSetting("WorkerControl:ApprovedHosts:0:IdentityFilePath", IdentityFilePath);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWorkerBridgeSessionFactory>();
            services.AddSingleton<IWorkerBridgeSessionFactory>(new FakeTaskBridgeSessionFactory(Fake));
        });
    }

    /// <summary>
    /// Turns the seeded employee's binding into a managed DeveloperContainer
    /// worker with an active session, comprehended orientation, enrolled
    /// enrollment and an authenticated running cursor. Idempotent.
    /// </summary>
    public (string EmployeeId, string WorkerId, string NativeSessionId) SeedManagedWorker()
    {
        lock (_seedLock)
        {
            _seed ??= SeedManagedWorkerCore();
            return _seed.Value;
        }
    }

    private (string EmployeeId, string WorkerId, string NativeSessionId) SeedManagedWorkerCore()
    {
        var store = Host.Organization!;
        var overview = store.GetOverview();
        var employee = Assert.Single(overview.Employees);
        var bindingId = employee.RuntimeBindingId;
        var nativeSessionId = employee.NativeSessionId ?? Host.GetStatus().SessionId!;
        const string workerId = "wrk-task-api";

        if (store.GetWorkerEnrollment(workerId) is null)
        {
            store.RegisterExecutionHost("host-task", "task.example", 22, "docker", "/known", new("host-task", "host-task", "Task Host"));
            var host = store.GetExecutionHost("host-task")!;
            store.RecordExecutionHostProbe("host-task", host.Revision, new ExecutionHostProbe(
                "ssh-ed25519", "SHA256:task", "sha256:" + new string('1', 64), "28", "1.48", "amd64", "overlay2", "ext4",
                false, 8_000_000_000, 8_000_000_000, 4, true, "linux/amd64", "valid"));

            Execute($"UPDATE runtime_bindings SET placement='DeveloperContainer', container_ref='task-container' WHERE id=$binding", ("$binding", bindingId));
            var keyPath = Path.Combine(_workerRoot, "worker.key");
            File.WriteAllBytes(keyPath, new byte[32]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var enrollment = store.CreateWorkerEnrollmentForPlan(bindingId, "host-task", "controller-task", "sha256:" + new string('2', 64), "linux/amd64", keyPath, "sha256:" + new string('3', 64), workerId);
            enrollment = store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "planned", "provisioning");
            store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "provisioning", "enrolled");
            store.RecordRemoteWorkerSession(bindingId, workerId, nativeSessionId, "Task session");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var orientationUpdated = Execute(
            """
            UPDATE orientation_assignments
            SET state = 'Comprehended',
                session_id = (SELECT session_ref FROM runtime_bindings WHERE id = $binding),
                comprehended_at = $now,
                evidence_hash = $hash,
                evidence_summary = 'ready',
                evidence_source = 'owner-submitted',
                last_error = NULL
            WHERE employee_id = (SELECT employee_id FROM runtime_bindings WHERE id = $binding)
            """,
            ("$now", now), ("$hash", "sha256:" + new string('5', 64)), ("$binding", bindingId));
        if (orientationUpdated == 0)
        {
            Execute(
                """
                INSERT INTO orientation_assignments (
                    id, employee_id, runtime_binding_id, session_id, policy_id, orientation_version, artifact_file_name,
                    artifact_bytes, state, assigned_at, delivered_at, acknowledged_at, comprehended_at, evidence_hash,
                    evidence_summary, evidence_source, required_runtime_generation, loaded_runtime_generation, last_error, revision)
                SELECT 'ori-task-api', b.employee_id, b.id, s.id, p.id, 'task-v1', 'orientation.md', 1, 'Comprehended',
                    $now, $now, $now, $now, $hash, 'ready', 'owner-submitted', NULL, NULL, NULL, 1
                FROM runtime_bindings b JOIN acp_sessions s ON s.id = b.session_ref, permission_policies p
                WHERE b.id = $binding
                """,
                ("$now", now), ("$hash", "sha256:" + new string('5', 64)), ("$binding", bindingId));
        }

        Execute("UPDATE dispatch_holds SET active=0 WHERE runtime_binding_id=$binding", ("$binding", bindingId));

        store.RecordWorkerStatusAndEvents(workerId, new ControllerWorkerStatus(1, 1, "running", null, null, 1, false, [], 0, 0), []);
        store.RecordWorkerConnectionState(workerId, "authenticated", 1);
        Fake.SessionId = nativeSessionId;
        return (employee.Id, workerId, nativeSessionId);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_workerRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        var path = Path.Combine(DataDirectory, OrganizationStore.DatabaseFileName);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }
}

internal sealed class FakeTaskBridgeSessionFactory(FakeTaskBridgeSession session) : IWorkerBridgeSessionFactory
{
    public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) =>
        Task.FromResult<IWorkerBridgeSession>(session);
}

/// <summary>
/// An in-process worker that answers status, submit, reconcile, replay and cancel
/// with exactly correlated identities and records every submit.
/// </summary>
public sealed class FakeTaskBridgeSession : IWorkerBridgeSession
{
    private readonly Dictionary<string, (string TurnId, long Epoch, long Process, string State, string? Outcome)> _requests = new(StringComparer.Ordinal);

    public FakeTaskBridgeSession() =>
        Lease = new WorkerBridgeLease(1, "controller-task", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);

    public WorkerBridgeLease Lease { get; }

    public string SessionId { get; set; } = string.Empty;

    public int SubmitCount { get; private set; }

    public void Complete(string requestId) =>
        _requests[requestId] = (_requests[requestId].TurnId, 1, 1, "completed", "{\"summary\":\"done\",\"changedPaths\":[\"src/a.cs\"],\"tests\":[{\"recipeId\":\"dotnet-test-release\",\"status\":\"passed\",\"summary\":\"passed\"}],\"deniedAction\":null,\"limitations\":[]}");

    public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) =>
        new Dictionary<string, object?>(fields) { ["operation"] = operation };

    public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(request, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        object value = operation switch
        {
            "status" => Status(),
            "submit" => Submit(root),
            "reconcile" => Reconcile(root),
            "replay" => new BridgeReplayPage([], false, root.GetProperty("afterSequence").GetInt64()),
            _ => new { ok = true },
        };
        return Task.FromResult(new WorkerSessionResult(operation, JsonSerializer.SerializeToElement(value, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions), mutation));
    }

    private object Submit(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetString()!;
        var turnId = root.GetProperty("turnId").GetString()!;
        SubmitCount++;
        _requests[requestId] = (turnId, 1, 1, "forwarded", null);
        return Stored(requestId, turnId, "forwarded", null);
    }

    private object Reconcile(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetString()!;
        if (!_requests.TryGetValue(requestId, out var recorded)) throw new WorkerRemoteException("worker-request-rejected");
        return Stored(requestId, recorded.TurnId, recorded.State, recorded.Outcome);
    }

    private object Stored(string requestId, string turnId, string state, string? outcome) => new
    {
        requestId,
        payloadHash = "sha256:" + new string('0', 64),
        state,
        outcomeJson = outcome,
        processGeneration = 1,
        ownershipEpoch = 1,
        turnId,
        sessionId = SessionId,
    };

    private BridgeWorkerStatus Status() => new(
        WorkerGeneration: 1,
        ProcessGeneration: 1,
        ProcessState: "running",
        LifecycleHandle: "life",
        ObservedPid: 42,
        ActiveRequestId: null,
        PendingPermission: null,
        OwnershipEpoch: 1,
        LeaseActive: true,
        DispatchHeld: false,
        HoldReason: null,
        HoldReasons: [],
        FirstRetainedSequence: 0,
        LastSequence: 0,
        AcknowledgedWorkerGeneration: 0,
        AcknowledgedSequence: 0,
        ReplayLoss: null,
        JournalFailure: null,
        ReplayGapCount: 0,
        ReplayGaps: [],
        ViewerSupported: true,
        ViewerAvailable: true,
        AcpInitialized: true,
        SessionId: SessionId);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// With the control runtime disabled every task route fails closed: reads with a
/// sanitized 503, mutations with a sanitized 503 only after same-origin.
/// </summary>
public sealed class EmployeeTaskApiDisabledRuntimeTests : IClassFixture<DisabledRuntimeFactory>
{
    private readonly DisabledRuntimeFactory _factory;

    public EmployeeTaskApiDisabledRuntimeTests(DisabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/employees/emp-test/tasks")]
    [InlineData("/api/tasks/tsk-test")]
    public async Task ReadsAre503ProblemDetails(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await RemoteWorkerApi.ProblemAsync(response);
    }

    [Fact]
    public async Task CrossOriginMutationsAre403BeforeStore()
    {
        using var client = _factory.CreateClient();
        var body = """{"expectedEmployeeRevision":1,"idempotencyKey":"k","taskSpec":{"description":"d","workspaceRoot":"/workspace/p","allowedPaths":["."],"allowedTools":["read"],"forbiddenActions":["x"],"maximumSeconds":10}}""";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/employees/emp-test/tasks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        create.Headers.Add("Origin", "https://other.example");
        using var response = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RemoteWorkerApi.ProblemAsync(response);
    }
}

/// <summary>
/// WorkerControl enabled with an unusable configuration: reads still work from
/// the store, and every dispatch or cancellation fails closed with a 409 rather
/// than a raw 500.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class EmployeeTaskApiWorkerDisabledTests : IClassFixture<WorkerControlInvalidConfigFactory>
{
    private readonly WorkerControlInvalidConfigFactory _factory;

    public EmployeeTaskApiWorkerDisabledTests(WorkerControlInvalidConfigFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateIs409WhenWorkerControlConfigurationIsUnusable()
    {
        using var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);

        using var portal = await client.GetAsync("/api/organization/portal");
        using var portalDocument = JsonDocument.Parse(await portal.Content.ReadAsStringAsync());
        var employeeId = Assert.Single(portalDocument.RootElement.GetProperty("employees").EnumerateArray()).GetProperty("id").GetString()!;

        using var response = await PostAsync(client, $"/api/employees/{employeeId}/tasks", """{"expectedEmployeeRevision":1,"idempotencyKey":"k","taskSpec":{"description":"d","workspaceRoot":"/workspace/p","allowedPaths":["."],"allowedTools":["read"],"forbiddenActions":["x"],"maximumSeconds":10}}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }
}
