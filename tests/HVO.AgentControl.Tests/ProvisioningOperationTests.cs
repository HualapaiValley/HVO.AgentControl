using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        Assert.Equal(HttpStatusCode.OK, prematureReconciliation.StatusCode);
        var held = (await prematureReconciliation.Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.AwaitingHostAuthority, held.State);
        Assert.True(held.ReconcileRequested);

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
        Assert.Null(app.Services.GetService<IProvisionAttemptLedger>());
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
            OperationId = Guid.Parse(competingOperation.Id).ToString("D")
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
    public async Task ClearedCapacityCannotAuthorizeAnAlreadyAcquiredAttempt()
    {
        await using var app = new TestApp();
        var ready = await Ready(app, "capacity-clear");
        var ledger = new DbProvisionAttemptLedger(app.Store);
        await using var admission = await ledger.Acquire(ready.Intent, ProvisionAction.CreateOrObserve, default);

        var secondStore = SeparateStore(app);
        var current = await secondStore.ProvisionOperation(ready.Operation.Id);
        var held = await secondStore.RequestProvisionReconciliation(current.Id, new(current.Revision));
        Assert.Equal(ProvisionOperationState.AwaitingCapacity, held.State);
        var denied = await Assert.ThrowsAsync<ProvisionAdmissionException>(() =>
            admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));
        Assert.Equal("fresh_capacity_reservation_required", denied.Code);
        await secondStore.ApproveProvisionCapacity(current.Id,
            new("capacity-replaced", 2, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));
        var stale = await Assert.ThrowsAsync<ProvisionAdmissionException>(() =>
            admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));
        Assert.Equal("fresh_capacity_reservation_required", stale.Code);
    }

    [Fact]
    public async Task ExpiredCapacityCannotAuthorizeAnAlreadyAcquiredAttempt()
    {
        await using var app = new TestApp();
        var ready = await Ready(app, "capacity-expired");
        var ledger = new DbProvisionAttemptLedger(app.Store);
        await using var admission = await ledger.Acquire(ready.Intent, ProvisionAction.CreateOrObserve, default);

        var secondStore = SeparateStore(app);
        await secondStore.Write(async db =>
        {
            var operation = await db.ProvisionOperations.SingleAsync(x => x.Id == ready.Operation.Id);
            operation.CapacityValidUntil = ControlStore.Now - 1;
            return true;
        });
        var denied = await Assert.ThrowsAsync<ProvisionAdmissionException>(() =>
            admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));
        Assert.Equal("fresh_capacity_reservation_required", denied.Code);
    }

    [Fact]
    public async Task DisposedAttemptCannotAuthorizeAnEffect()
    {
        await using var app = new TestApp();
        var ready = await Ready(app, "capacity-disposed");
        var admission = await new DbProvisionAttemptLedger(app.Store).Acquire(ready.Intent, ProvisionAction.CreateOrObserve, default);
        await admission.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));
        Assert.Empty((await app.Store.ProvisionOperation(ready.Operation.Id)).Effects);
    }

    [Fact]
    public async Task ChangedResourceIdCannotAuthorizeAnotherUpEffect()
    {
        await using var app = new TestApp();
        var ready = await Ready(app, "capacity-resource");
        await using var admission = await new DbProvisionAttemptLedger(app.Store)
            .Acquire(ready.Intent, ProvisionAction.CreateOrObserve, default);
        Assert.True(await admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));

        var denied = await Assert.ThrowsAsync<ProvisionAdmissionException>(() =>
            admission.TryBeginEffect("up", Id(), default));
        Assert.Equal("invalid_effect_identity", denied.Code);
        Assert.Single((await app.Store.ProvisionOperation(ready.Operation.Id)).Effects);
    }

    [Fact]
    public async Task ApprovedIntentIsImmutableAcrossReplayAndRestart()
    {
        string data;
        string secrets;
        ProvisionIntent intent;
        string operationId;
        await using (var app = new TestApp())
        {
            var ready = await Ready(app, "capacity-authority");
            intent = ready.Intent;
            operationId = ready.Operation.Id;
            var replay = await app.Store.ApproveProvisionAuthority(operationId, intent);
            Assert.Equal(ProvisionOperationState.AwaitingExecution, replay.State);
            Assert.Equal("capacity-authority", replay.CapacityReservationId);
            Assert.Equal(ready.Operation.Revision, replay.Revision);
            await using var admission = await new DbProvisionAttemptLedger(app.Store)
                .Acquire(intent, ProvisionAction.CreateOrObserve, default);
            var changedWhileHeld = intent with { AuthorityRevision = intent.AuthorityRevision + 1, Digest = new string('e', 64) };
            await Assert.ThrowsAsync<ProvisionIntentConflictException>(() =>
                app.Store.ApproveProvisionAuthority(operationId, changedWhileHeld));
            Assert.True(await admission.TryBeginEffect("up", ready.Input.WorkspaceId, default));
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var changed = intent with { AuthorityRevision = intent.AuthorityRevision + 1, Digest = new string('e', 64) };
        await Assert.ThrowsAsync<ProvisionIntentConflictException>(() =>
            restarted.Store.ApproveProvisionAuthority(operationId, changed));
        var persisted = await restarted.Store.ProvisionOperation(operationId);
        Assert.Equal(intent.AuthorityRevision, persisted.AuthorityRevision);
        Assert.Equal(intent.Digest, persisted.IntentDigest);
        Assert.Equal("capacity-authority", persisted.CapacityReservationId);
    }

    [Fact]
    public async Task DifferentWorkspaceIdsForSameCanonicalDirectoryShareDurableAdmission()
    {
        await using var app = new TestApp();
        var first = await Ready(app, "capacity-directory");
        var secondInput = first.Input with { RequestId = Id(), WorkspaceId = Id() };
        var secondOperation = await app.Store.CreateProvisionOperation(secondInput);
        var secondIntent = Intent(secondOperation, secondInput) with
        {
            Workspace = Intent(secondOperation, secondInput).Workspace with { Directory = first.Intent.Workspace.Directory + "/." }
        };
        await app.Store.ApproveProvisionAuthority(secondOperation.Id, secondIntent);
        await app.Store.ApproveProvisionCapacity(secondOperation.Id,
            new("capacity-directory-2", 1, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));

        var firstLedger = new DbProvisionAttemptLedger(app.Store);
        var firstAdmission = await firstLedger.Acquire(first.Intent, ProvisionAction.CreateOrObserve, default);
        var secondLedger = new DbProvisionAttemptLedger(SeparateStore(app));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                secondLedger.Acquire(secondIntent, ProvisionAction.CreateOrObserve, timeout.Token));
        await firstAdmission.DisposeAsync();
        var denied = await Assert.ThrowsAsync<ProvisionAdmissionException>(() =>
            secondLedger.Acquire(secondIntent, ProvisionAction.CreateOrObserve, default));
        Assert.Equal("workspace_admission_held_for_reconciliation", denied.Code);
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

    [Fact]
    public async Task AuthenticatedExecutorAtomicallyConsumesPermitAndReportsImmutableSuccess()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var host = await app.Store.Host(setup.Input.HostId);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var credential = Digest(secret);
        var executor = await app.Store.CreateHostExecutor(new(Id(), Id(), host.Id, host.Revision,
            Digest("endpoint"), Digest("physical"), Digest("engine"), Digest("builder"), Digest("cli"),
            Digest("root"), Digest("authority"), credential));
        var pending = new HostExecutorPrincipal(executor.Id, host.Id, executor.AuthorityGeneration,
            HostExecutorState.Pending, credential);
        await app.Store.ActivateHostExecutor(pending,
            new(executor.Id, host.Id, executor.AuthorityGeneration, Digest("authority"), Digest("boot"), Digest("incarnation")));
        var principal = pending with { State = HostExecutorState.Active };
        var machine = app.CreateClient();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", executor.Id + "." + secret);
        var operation = await app.Store.CreateProvisionOperation(setup.Input);
        var intent = Intent(operation, setup.Input) with { AuthorityRevision = executor.AuthorityGeneration };
        var authorityResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/authority",
            new ApproveHostProvisionAuthorityInput(intent));
        Assert.Equal(HttpStatusCode.OK, authorityResponse.StatusCode);
        operation = (await authorityResponse.Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        var policy = await app.Store.ConfigureHostResourcePolicy(new(Id(), Id(), executor.Id, 0,
            8000, 8 * 1024L * 1024 * 1024, 100 * 1024L * 1024 * 1024, 2,
            500, 512 * 1024 * 1024, 1024 * 1024 * 1024, 120, 300));
        var now = ControlStore.Now;
        var observation = await app.Store.SubmitHostResourceObservation(principal, new(Id(), executor.Id, host.Id,
            Digest("endpoint"), Digest("physical"), Digest("engine"), Digest("builder"), executor.AuthorityGeneration,
            Digest("boot"), Digest("incarnation"), 1, 1, HostObservationState.Complete, now - 1000, now, "x86_64",
            8000, 7000, 8 * 1024L * 1024 * 1024, 7 * 1024L * 1024 * 1024, 0, 2 * 1024L * 1024 * 1024,
            512 * 1024 * 1024, 1024 * 1024 * 1024, 0, setup.Input.WorkspaceId,
            ControlStore.ProvisionWorkspaceDigest(intent.Workspace.Directory), Digest("workspace-fs"),
            80 * 1024L * 1024 * 1024, 1_000_000, Digest("docker-fs"), 70 * 1024L * 1024 * 1024,
            1_000_000, true, 2));
        var reservation = await app.Store.AcquireHostResourceReservation(new(Id(), Id(), intent.Digest, host.Id,
            executor.Id, policy.Id, policy.Revision, observation.Id, setup.Input.WorkspaceId, operation.Id, "Build",
            3000, BuildMemory + RuntimeMemory, 1024 * 1024 * 1024, 1));
        using (var owner = await app.SignIn())
        {
            var capacityResponse = await owner.PostAsJsonAsync($"/api/v1/provisioning/operations/{operation.Id}/capacity",
                new BindProvisionCapacityInput(operation.Revision, reservation.Id));
            Assert.Equal(HttpStatusCode.OK, capacityResponse.StatusCode);
        }
        var claimResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/claim",
            new ClaimHostProvisioningInput(reservation.Id));
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var assignment = (await claimResponse.Content.ReadFromJsonAsync<HostProvisioningAssignment>())!;

        Assert.Equal(intent.Digest, assignment.Intent.Digest);
        Assert.Equal(reservation.Id, assignment.CapacityReservationId);
        var otherSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var other = await app.Store.CreateHostExecutor(new(Id(), Id(), host.Id, host.Revision, Digest("other-endpoint"),
            Digest("physical"), Digest("other-engine"), Digest("other-builder"), Digest("other-cli"), Digest("other-root"),
            Digest("other-authority"), Digest(otherSecret)));
        var otherPending = new HostExecutorPrincipal(other.Id, host.Id, other.AuthorityGeneration, HostExecutorState.Pending, Digest(otherSecret));
        await app.Store.ActivateHostExecutor(otherPending, new(other.Id, host.Id, other.AuthorityGeneration,
            Digest("other-authority"), Digest("other-boot"), Digest("other-incarnation")));
        using var otherMachine = app.CreateClient();
        otherMachine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Id + "." + otherSecret);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherMachine.GetAsync($"/api/v1/host-executor/provisioning/{operation.Id}")).StatusCode);
        var effectInput = new BeginHostProvisionEffectInput(assignment.ClaimGeneration, reservation.Id,
            reservation.Revision, reservation.GrantGeneration, intent.Digest, setup.Input.WorkspaceId, observation.Id);
        await app.Store.Write(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER AbortProvisionEffect BEFORE INSERT ON ProvisionEffects BEGIN SELECT RAISE(ABORT, 'proof rollback'); END;");
            return true;
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/begin-effect", effectInput)).StatusCode);
        await app.Store.Read(async db =>
        {
            Assert.Equal(HostReservationState.Held, (await db.HostResourceReservations.FindAsync(reservation.Id))!.State);
            Assert.Empty(await db.ProvisionEffects.Where(x => x.OperationId == operation.Id).ToListAsync());
            Assert.Equal(ProvisionOperationState.AwaitingExecution, (await db.ProvisionOperations.SingleAsync(x => x.Id == operation.Id)).State);
            return true;
        });
        await app.Store.Write(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER AbortProvisionEffect;");
            return true;
        });
        var effectResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/begin-effect", effectInput);
        Assert.Equal(HttpStatusCode.OK, effectResponse.StatusCode);
        var effect = (await effectResponse.Content.ReadFromJsonAsync<HostProvisionEffectDecision>())!;
        Assert.True(effect.AuthorizedNow);
        var replay = (await (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/begin-effect", effectInput))
            .Content.ReadFromJsonAsync<HostProvisionEffectDecision>())!;
        Assert.False(replay.AuthorizedNow);
        await app.Store.Read(async db =>
        {
            Assert.Equal(HostReservationState.EffectCommitted, (await db.HostResourceReservations.FindAsync(reservation.Id))!.State);
            Assert.Single(await db.ProvisionEffects.Where(x => x.OperationId == operation.Id).ToListAsync());
            return true;
        });

        var progressInput = new HostProvisionProgressInput(Id(), assignment.ClaimGeneration, 1, "cli-up");
        var progress = (await (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/progress", progressInput))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal("cli-up", Assert.Single(progress.Progress).Stage);
        Assert.Equal("", Assert.Single(progress.Progress).Text);
        Assert.Equal(progress.Revision, (await (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/progress", progressInput)).Content.ReadFromJsonAsync<ProvisionOperationView>())!.Revision);

        var observed = new ProvisionContainerObservation(new string('a', 64), "sha256:" + new string('b', 64),
            "fixture@sha256:" + new string('c', 64), "vscode", true, intent.Labels,
            [new("bind", intent.Workspace.Directory, intent.Workspace.ContainerWorkspace, null, true)],
            setup.Input.RequestedRuntimeCpuMillis, setup.Input.RequestedRuntimeMemoryBytes);
        var executed = new ProvisionExecutionEvidence("vscode", "1000", intent.Workspace.ContainerWorkspace,
            new Dictionary<string, string> { ["dotnet"] = "10.0.400" }.ToImmutableDictionary());
        var resultId = Id();
        var resultInput = new HostProvisionResultInput(resultId, assignment.ClaimGeneration, 2,
            Guid.Parse(operation.Id).ToString("D"), intent.Digest, "VerifiedEnvironment", "verified", observed, executed, []);
        var wrongRoot = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/result",
            resultInput with { ReportId = Id(), Observed = observed with { Mounts = [new("bind", "/wrong", intent.Workspace.ContainerWorkspace, null, true)] } });
        Assert.Equal(HttpStatusCode.Conflict, wrongRoot.StatusCode);
        var noTools = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/result",
            resultInput with { ReportId = Id(), Executed = executed with { Tools = ImmutableDictionary<string, string>.Empty } });
        Assert.Equal(HttpStatusCode.Conflict, noTools.StatusCode);
        var result = (await (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/result", resultInput))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.AwaitingEnrollment, result.State);
        Assert.Equal(observed.ContainerId, result.ObservedContainerId);
        var revision = result.Revision;
        Assert.Equal(revision, (await (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/result", resultInput))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!.Revision);
        var late = (await (await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/result",
            resultInput with { ReportId = Id(), Sequence = 3, State = "Failed", Code = "late_failure", Observed = null, Executed = null }))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.AwaitingEnrollment, late.State);
        Assert.Equal(observed.ContainerId, late.ObservedContainerId);
        Assert.Equal(revision, late.Revision);
        using var terminalOwner = await app.SignIn();
        Assert.Equal(HttpStatusCode.Conflict, (await terminalOwner.PostAsJsonAsync(
            $"/api/v1/provisioning/operations/{operation.Id}/cancel", new ProvisionOperationControlInput(late.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await terminalOwner.PostAsJsonAsync(
            $"/api/v1/provisioning/operations/{operation.Id}/reconcile", new ProvisionOperationControlInput(late.Revision))).StatusCode);
    }

    [Fact]
    public async Task VerifiedEnvironmentRejectsContradictoryOrRootUidForNonRootUser()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var host = await app.Store.Host(setup.Input.HostId);
        var (machine, assignment, operation, intent) = await EstablishClaimedEffect(app, setup, host);

        // A non-root approved user (vscode) cannot claim UID 0 or a negative UID even when
        // the effective RemoteUser and all workspace/tool evidence otherwise match (finding 3).
        var positive = new ProvisionExecutionEvidence("vscode", "1000", intent.Workspace.ContainerWorkspace, Tools());
        var rootClaim = new ProvisionExecutionEvidence("vscode", "0", intent.Workspace.ContainerWorkspace, Tools());
        var negativeClaim = new ProvisionExecutionEvidence("vscode", "-5", intent.Workspace.ContainerWorkspace, Tools());
        HostProvisionResultInput Input(string reportId, ProvisionExecutionEvidence executed) => new(
            reportId, assignment.ClaimGeneration, 2, Guid.Parse(operation.Id).ToString("D"), intent.Digest,
            "VerifiedEnvironment", "verified", Observed(assignment, setup), executed, []);
        Assert.Equal(HttpStatusCode.Conflict, (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/result", Input(Id(), rootClaim))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/result", Input(Id(), negativeClaim))).StatusCode);
        var accepted = (await (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/result", Input(Id(), positive)))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.AwaitingEnrollment, accepted.State);
    }

    [Fact]
    public async Task ReportSequenceConflictGuardIsPreservedForRestartedProgress()
    {
        await using var app = new TestApp();
        var setup = await Setup(app);
        var host = await app.Store.Host(setup.Input.HostId);
        var (machine, assignment, operation, _) = await EstablishClaimedEffect(app, setup, host);

        // After a committed external effect the operation is Unknown; progress is still accepted.
        var first = new HostProvisionProgressInput(Id(), assignment.ClaimGeneration, 1, "cli-up");
        var acceptedProgress = (await (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/progress", first))
            .Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        Assert.Equal(ProvisionOperationState.Unknown, acceptedProgress.State);
        Assert.Equal("cli-up", Assert.Single(acceptedProgress.Progress).Stage);
        // A restarted executor must never reuse an acknowledged sequence; the server's
        // per-(operation, claim) sequence guard rejects it instead of stranding the run.
        var replay = new HostProvisionProgressInput(Id(), assignment.ClaimGeneration, 1, "cli-up");
        Assert.Equal(HttpStatusCode.Conflict, (await machine.PostAsJsonAsync(
            $"/api/v1/host-executor/provisioning/{operation.Id}/progress", replay)).StatusCode);
    }

    private static async Task<(HttpClient Machine, HostProvisioningAssignment Assignment, ProvisionOperationView Operation, ProvisionIntent Intent)>
        EstablishClaimedEffect(TestApp app, SetupResult setup, HostRecord host)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var credential = Digest(secret);
        var executor = await app.Store.CreateHostExecutor(new(Id(), Id(), host.Id, host.Revision,
            Digest("endpoint"), Digest("physical"), Digest("engine"), Digest("builder"), Digest("cli"),
            Digest("root"), Digest("authority"), credential));
        var pending = new HostExecutorPrincipal(executor.Id, host.Id, executor.AuthorityGeneration,
            HostExecutorState.Pending, credential);
        await app.Store.ActivateHostExecutor(pending,
            new(executor.Id, host.Id, executor.AuthorityGeneration, Digest("authority"), Digest("boot"), Digest("incarnation")));
        var principal = pending with { State = HostExecutorState.Active };
        var machine = app.CreateClient();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", executor.Id + "." + secret);
        var operation = await app.Store.CreateProvisionOperation(setup.Input);
        var intent = Intent(operation, setup.Input) with { AuthorityRevision = executor.AuthorityGeneration };
        var authorityResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/authority",
            new ApproveHostProvisionAuthorityInput(intent));
        Assert.Equal(HttpStatusCode.OK, authorityResponse.StatusCode);
        operation = (await authorityResponse.Content.ReadFromJsonAsync<ProvisionOperationView>())!;
        var policy = await app.Store.ConfigureHostResourcePolicy(new(Id(), Id(), executor.Id, 0,
            8000, 8 * 1024L * 1024 * 1024, 100 * 1024L * 1024 * 1024, 2,
            500, 512 * 1024 * 1024, 1024 * 1024 * 1024, 120, 300));
        var now = ControlStore.Now;
        var observation = await app.Store.SubmitHostResourceObservation(principal, new(Id(), executor.Id, host.Id,
            Digest("endpoint"), Digest("physical"), Digest("engine"), Digest("builder"), executor.AuthorityGeneration,
            Digest("boot"), Digest("incarnation"), 1, 1, HostObservationState.Complete, now - 1000, now, "x86_64",
            8000, 7000, 8 * 1024L * 1024 * 1024, 7 * 1024L * 1024 * 1024, 0, 2 * 1024L * 1024 * 1024,
            512 * 1024 * 1024, 1024 * 1024 * 1024, 0, setup.Input.WorkspaceId,
            ControlStore.ProvisionWorkspaceDigest(intent.Workspace.Directory), Digest("workspace-fs"),
            80 * 1024L * 1024 * 1024, 1_000_000, Digest("docker-fs"), 70 * 1024L * 1024 * 1024,
            1_000_000, true, 2));
        var reservation = await app.Store.AcquireHostResourceReservation(new(Id(), Id(), intent.Digest, host.Id,
            executor.Id, policy.Id, policy.Revision, observation.Id, setup.Input.WorkspaceId, operation.Id, "Build",
            3000, BuildMemory + RuntimeMemory, 1024 * 1024 * 1024, 1));
        using (var owner = await app.SignIn())
        {
            var capacityResponse = await owner.PostAsJsonAsync($"/api/v1/provisioning/operations/{operation.Id}/capacity",
                new BindProvisionCapacityInput(operation.Revision, reservation.Id));
            Assert.Equal(HttpStatusCode.OK, capacityResponse.StatusCode);
        }
        var claimResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/claim",
            new ClaimHostProvisioningInput(reservation.Id));
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var assignment = (await claimResponse.Content.ReadFromJsonAsync<HostProvisioningAssignment>())!;
        var effectInput = new BeginHostProvisionEffectInput(assignment.ClaimGeneration, reservation.Id,
            reservation.Revision, reservation.GrantGeneration, intent.Digest, setup.Input.WorkspaceId, observation.Id);
        var effectResponse = await machine.PostAsJsonAsync($"/api/v1/host-executor/provisioning/{operation.Id}/begin-effect", effectInput);
        Assert.Equal(HttpStatusCode.OK, effectResponse.StatusCode);
        return (machine, assignment, operation, intent);
    }

    private static ProvisionContainerObservation Observed(HostProvisioningAssignment assignment, SetupResult setup) => new(
        new string('a', 64), "sha256:" + new string('b', 64), "fixture@sha256:" + new string('c', 64),
        "vscode", true, assignment.Intent.Labels,
        [new("bind", assignment.Intent.Workspace.Directory, assignment.Intent.Workspace.ContainerWorkspace, null, true)],
        setup.Input.RequestedRuntimeCpuMillis, setup.Input.RequestedRuntimeMemoryBytes);

    private static ImmutableDictionary<string, string> Tools() =>
        new Dictionary<string, string> { ["dotnet"] = "10.0.400" }.ToImmutableDictionary();

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
            input.ConfigurationSha256, "vscode", "/workspaces/source", [new("dotnet", ["dotnet", "--version"], "10.0.400")]);
        var operationId = Guid.Parse(operation.Id).ToString("D");
        var digest = new string('d', 64);
        var labels = new Dictionary<string, string>
        {
            ["hvo.agentcontrol.provisioner"] = "official-cli-v1",
            ["hvo.agentcontrol.operation"] = operationId,
            ["hvo.agentcontrol.host"] = operation.HostId,
            ["hvo.agentcontrol.workspace"] = workspace.Id,
            ["hvo.agentcontrol.intent"] = digest,
            ["hvo.agentcontrol.source"] = workspace.SourceRevision,
            ["hvo.agentcontrol.config"] = workspace.ConfigurationSha256
        }.ToImmutableDictionary(StringComparer.Ordinal);
        return new(operationId, operation.HostId, 3, workspace,
            LocalDevContainerRunner.CliVersion, LocalDevContainerRunner.CliSha256, input.ColdBuild,
            digest, labels);
    }

    private static async Task<ReadyResult> Ready(TestApp app, string reservationId)
    {
        var setup = await Setup(app);
        var operation = await app.Store.CreateProvisionOperation(setup.Input);
        var intent = Intent(operation, setup.Input);
        await app.Store.ApproveProvisionAuthority(operation.Id, intent);
        operation = await app.Store.ApproveProvisionCapacity(operation.Id,
            new(reservationId, 1, ControlStore.Now + 60000, 2000, BuildMemory, 1000, RuntimeMemory));
        return new(setup.Input, operation, intent);
    }

    private static ControlStore SeparateStore(TestApp app) => new(
        app.Services.GetRequiredService<IDbContextFactory<ControlDb>>(),
        app.Services.GetRequiredService<IOptions<ControlOptions>>(),
        app.Services.GetRequiredService<Secrets>());

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record SetupResult(CreateProvisionOperationInput Input);
    private sealed record ReadyResult(CreateProvisionOperationInput Input, ProvisionOperationView Operation, ProvisionIntent Intent);
}
