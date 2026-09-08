using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProvisioningOperationTests
{
    private const long BuildMemory = 2L * 1024 * 1024 * 1024;
    private const long RuntimeMemory = 1024L * 1024 * 1024;
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task OwnerApiPersistsHeldIntentAndRejectsInjectedExecutionAuthority()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var route = "/api/v1/provisioning/operations";
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(route, setup.Input)).StatusCode);

        using var missingCsrf = await app.SignIn();
        missingCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await missingCsrf.PostAsJsonAsync(route, setup.Input)).StatusCode);

        using var owner = await app.SignIn();
        var accepted = await owner.PostAsJsonAsync(route, setup.Input);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(route + "/" + setup.Input.RequestId, accepted.Headers.Location?.ToString());
        var operation = (await accepted.Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.AwaitingHostAuthority, operation.State);
        Assert.Equal("trusted_host_executor_required", operation.Code);
        Assert.False(operation.EffectStarted);
        var prematureReconciliation = await owner.PostAsJsonAsync(route + "/" + operation.Id + "/reconcile", new ProvisionOperationControlInput(operation.Revision));
        Assert.Equal(HttpStatusCode.Conflict, prematureReconciliation.StatusCode);
        Assert.Equal("reconciliation_not_required", (await prematureReconciliation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(route, setup.Input)).StatusCode);
        var conflict = await owner.PostAsJsonAsync(route, setup.Input with { SourceRevision = new string('c', 40) });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var injected = await owner.PostAsJsonAsync(route, new
        {
            setup.Input.RequestId,
            setup.Input.HostId,
            setup.Input.ExpectedHostRevision,
            setup.Input.RuntimeId,
            setup.Input.ExpectedRuntimeRevision,
            setup.Input.ExpectedEnvironmentRevision,
            setup.Input.ProjectId,
            setup.Input.ExpectedProjectRevision,
            setup.Input.WorkspaceId,
            setup.Input.SourceRevision,
            setup.Input.ConfigurationPath,
            setup.Input.ConfigurationSha256,
            setup.Input.RequestedBuildCpuMillis,
            setup.Input.RequestedBuildMemoryBytes,
            setup.Input.RequestedRuntimeCpuMillis,
            setup.Input.RequestedRuntimeMemoryBytes,
            dockerSocket = "/var/run/docker.sock"
        });
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.Null(app.Services.GetService<LocalDevContainerRunner>());
        Assert.IsType<DbProvisionAttemptLedger>(app.Services.GetRequiredService<IProvisionAttemptLedger>());
    }

    [Fact]
    public async Task OperationAndCancellationSurviveWebRestartWithoutInventingAnEffect()
    {
        string data;
        string secrets;
        CreateProvisionOperationInput input;
        await using (var app = new TestApp())
        {
            var setup = await Setup(app);
            input = setup.Input;
            await app.Store.CreateProvisionOperation(input);
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var recovered = await restarted.Store.ProvisionOperation(input.RequestId);
        Assert.Equal(ProvisionOperationState.AwaitingHostAuthority, recovered.State);
        var cancelled = await restarted.Store.CancelProvisionOperation(recovered.Id, new(recovered.Revision));
        Assert.Equal(ProvisionOperationState.Cancelled, cancelled.State);
        Assert.False(cancelled.EffectStarted);
        Assert.Empty(cancelled.Effects);
        Assert.Equal(0, await restarted.Store.Read(db => db.ProvisionAttempts.CountAsync()));
    }

    [Fact]
    public async Task LedgerRequiresCapacitySerializesAdmissionAndNeverRepeatsEffectAfterRestart()
    {
        string data;
        string secrets;
        ProvisionIntent intent;
        CreateProvisionOperationInput requested;
        await using (var app = new TestApp())
        {
            var setup = await Setup(app);
            requested = setup.Input;
            var operation = await app.Store.CreateProvisionOperation(setup.Input);
            intent = Intent(operation, setup.Input);
            await app.Store.ApproveProvisionAuthority(operation.Id, intent);
            var ledger = new DbProvisionAttemptLedger(app.Store);
            var denied = await Assert.ThrowsAsync<ProvisionAdmissionException>(() => ledger.Acquire(intent, ProvisionAction.CreateOrObserve, default));
            Assert.Equal("fresh_capacity_reservation_required", denied.Code);
            var insufficient = await Assert.ThrowsAsync<ProvisionAdmissionException>(() => app.Store.ApproveProvisionCapacity(operation.Id,
                new("capacity-too-small", 1, ControlStore.Now + 60000, 1000, BuildMemory, 500, RuntimeMemory)));
            Assert.Equal("valid_capacity_reservation_required", insufficient.Code);

            await app.Store.ApproveProvisionCapacity(operation.Id,
                new("capacity-1", 7, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));
            await using (var admission = await ledger.Acquire(intent, ProvisionAction.CreateOrObserve, default))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.Acquire(intent, ProvisionAction.CreateOrObserve, timeout.Token));
                Assert.True(await admission.TryBeginEffect("up", setup.Input.WorkspaceId, default));
                Assert.False(await admission.TryBeginEffect("up", setup.Input.WorkspaceId, default));
            }
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var ledgerAfterRestart = new DbProvisionAttemptLedger(restarted.Store);
        await using (var replay = await ledgerAfterRestart.Acquire(intent, ProvisionAction.CreateOrObserve, default))
        {
            Assert.True(await replay.HasEffect("up", intent.Workspace.Id, default));
            Assert.False(await replay.TryBeginEffect("up", intent.Workspace.Id, default));
        }
        var operationAfterRestart = await restarted.Store.ProvisionOperation(intent.OperationId);
        Assert.Equal(ProvisionOperationState.Unknown, operationAfterRestart.State);
        Assert.True(operationAfterRestart.EffectStarted);
        Assert.Single(operationAfterRestart.Effects);
        await Assert.ThrowsAsync<ProvisionIntentConflictException>(() => ledgerAfterRestart.Acquire(intent with { Digest = new string('f', 64) }, ProvisionAction.CreateOrObserve, default));
        var competingOperation = await restarted.Store.CreateProvisionOperation(requested with { RequestId = Id() });
        var competingIntent = Intent(competingOperation, requested) with
        {
            OperationId = Guid.Parse(competingOperation.Id).ToString("D"),
            Digest = new string('e', 64)
        };
        await restarted.Store.ApproveProvisionAuthority(competingOperation.Id, competingIntent);
        await restarted.Store.ApproveProvisionCapacity(competingOperation.Id,
            new("capacity-competitor", 1, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));
        var workspaceAdmission = await Assert.ThrowsAsync<ProvisionAdmissionException>(() => ledgerAfterRestart.Acquire(competingIntent, ProvisionAction.CreateOrObserve, default));
        Assert.Equal("workspace_admission_held_for_reconciliation", workspaceAdmission.Code);
    }

    [Fact]
    public async Task CancellationAfterEffectKeepsUnknownAndStillPermitsReadOnlyReconciliation()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var operation = await app.Store.CreateProvisionOperation(setup.Input);
        var intent = Intent(operation, setup.Input);
        await app.Store.ApproveProvisionAuthority(operation.Id, intent);
        await app.Store.ApproveProvisionCapacity(operation.Id,
            new("capacity-2", 1, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));
        var ledger = new DbProvisionAttemptLedger(app.Store);
        await using (var admission = await ledger.Acquire(intent, ProvisionAction.CreateOrObserve, default))
            Assert.True(await admission.TryBeginEffect("up", setup.Input.WorkspaceId, default));

        var current = await app.Store.ProvisionOperation(operation.Id);
        var cancelled = await app.Store.CancelProvisionOperation(operation.Id, new(current.Revision));
        Assert.Equal(ProvisionOperationState.Unknown, cancelled.State);
        Assert.Equal("cancellation_requested_external_effect_unresolved", cancelled.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.Acquire(intent, ProvisionAction.CreateOrObserve, default));
        var requested = await app.Store.RequestProvisionReconciliation(operation.Id, new(cancelled.Revision));
        Assert.True(requested.ReconcileRequested);
        Assert.Equal("reconciliation_requested_external_effect_unresolved", requested.Code);
        await using var reconciliation = await ledger.Acquire(intent, ProvisionAction.Observe, default);
        Assert.True(await reconciliation.HasEffect("up", setup.Input.WorkspaceId, default));
    }

    [Fact]
    public async Task DurableProgressAndResultExcludeRawCliOutputAndResolvedConfiguration()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var operation = await app.Store.CreateProvisionOperation(setup.Input);
        var secret = "TOP-SECRET-EXTERNAL-OUTPUT";
        await app.Store.RecordProvisionProgress(operation.Id, new(1, "read-configuration", secret));
        using var resolved = JsonDocument.Parse("{\"containerEnv\":{\"TOKEN\":\"" + secret + "\"}}");
        var result = new HostProvisionResult(Guid.Parse(operation.Id).ToString("D"), "Unknown", "up_interrupted_reconcile_required", false,
            null, resolved.RootElement.Clone(), null, null, ImmutableArray<ProvisionProgress>.Empty, ["workspace-retained"]);
        await app.Store.RecordProvisionResult(operation.Id, result);

        var saved = await app.Store.Read(async db => (await db.ProvisionOperations.SingleAsync(x => x.Id == operation.Id)).ResultJson);
        Assert.DoesNotContain(secret, saved, StringComparison.Ordinal);
        Assert.DoesNotContain("containerEnv", saved, StringComparison.Ordinal);
        var view = await app.Store.ProvisionOperation(operation.Id);
        Assert.Equal("read-configuration", Assert.Single(view.Progress).Stage);
        Assert.Equal("", Assert.Single(view.Progress).Text);
    }

    private static async Task<SetupResult> Setup(TestApp app)
    {
        var host = await app.Store.CreateHost(new(Id(), Id(), "Provision host", "PhysicalMachine"));
        var project = await app.Store.CreateProject(new(Id(), Id(), "Provision source", "https://github.com/example/provision-source"));
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        const string configurationPath = ".devcontainer/devcontainer.json";
        var environment = await app.Store.ConfigureRuntimeEnvironment(runtime.Id,
            new(Id(), 0, runtime.Revision, host.Id, RuntimeEnvironmentKind.ManagedDevcontainer, project.Id, configurationPath));
        var input = new CreateProvisionOperationInput(Id(), host.Id, host.Revision, runtime.Id, environment.RuntimeRevision,
            environment.Revision, project.Id, project.Revision, Id(), new string('a', 40), configurationPath,
            new string('b', 64), 2000, BuildMemory, 1000, RuntimeMemory);
        return new(input);
    }

    private static ProvisionIntent Intent(ProvisionOperationView operation, CreateProvisionOperationInput input)
    {
        var workspace = new ApprovedProvisionWorkspace(input.WorkspaceId, "/srv/workspaces/" + input.WorkspaceId,
            "https://github.com/example/provision-source", input.SourceRevision, input.ConfigurationPath,
            input.ConfigurationSha256, "vscode", "/workspaces/source", ImmutableArray<ProvisionToolProbe>.Empty);
        return new(Guid.Parse(operation.Id).ToString("D"), operation.HostId, 3, workspace,
            LocalDevContainerRunner.CliVersion, LocalDevContainerRunner.CliSha256, input.ColdBuild,
            new string('d', 64), ImmutableDictionary<string, string>.Empty);
    }

    private sealed record SetupResult(CreateProvisionOperationInput Input);
}
