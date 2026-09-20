using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Focused tests for the deliberate container replacement and the managed hire
/// provisioning/orientation coordinator. They run entirely with fakes: no SSH,
/// no Docker, no provider. The verified profile build is created through the real
/// store state machine and the approval/employee/binding is allocated by the
/// store, so the coordinator consumes the same frozen records as production.
/// </summary>
public sealed class HireProvisioningCoordinatorTests
{
    private const string BaseDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string BuiltDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ContextHash = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";
    private const string RebuildDigest = "sha256:" + "4444444444444444444444444444444444444444444444444444444444444444";

    [Fact]
    public async Task ReplaceRemovesAndRecreatesTheExactOwnedContainerAndLoadsTheAuthoritativeSession()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        var stored = fixture.Store.GetWorkerEnrollment(enrollment.WorkerId)!;
        var beforeEffects = fixture.Provisioner.Effects.Count;
        var volumeEffectsBefore = fixture.Provisioner.Effects.Count(x => x.StartsWith("volume:", StringComparison.Ordinal));
        var originalContainerRef = stored.ContainerRef;

        var result = await fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None);

        var effects = fixture.Provisioner.Effects.Skip(beforeEffects).ToArray();
        Assert.Contains("stop:" + enrollment.ContainerName, effects);
        Assert.Contains("remove-container:" + enrollment.ContainerName, effects);
        Assert.Contains("container:" + enrollment.ContainerName, effects);
        Assert.Contains("start:" + enrollment.ContainerName, effects);
        // Volumes are never removed or recreated by a replacement.
        Assert.Equal(volumeEffectsBefore, fixture.Provisioner.Effects.Count(x => x.StartsWith("volume:", StringComparison.Ordinal)));
        Assert.DoesNotContain(effects, x => x.StartsWith("remove-volume:", StringComparison.Ordinal));

        // The fresh container reproduces the exact name, image, platform, four
        // volumes and frozen limits.
        var created = fixture.Provisioner.Containers.Last();
        Assert.Equal(enrollment.ContainerName, created.Name);
        Assert.Equal(enrollment.ExpectedImageDigest, created.ImageDigest);
        Assert.Equal(enrollment.ExpectedPlatform, created.Platform);
        Assert.Equal(
            [enrollment.ControlVolumeName, enrollment.HomeVolumeName, enrollment.WorkspaceVolumeName, enrollment.SessionVolumeName],
            created.Volumes.Select(v => v.Name).ToArray());

        // The replacement process loaded the authoritative native session and the
        // result reports the advanced generation.
        Assert.Contains(fixture.VerificationSession.Invocations, x => x.Operation == "load-session");
        Assert.Equal(fixture.NativeSessionId, result.NativeSessionId);
        Assert.Equal(fixture.NativeSessionId, result.Status.SessionId);
        Assert.True(result.ProcessGeneration > 0);

        var replaced = fixture.Store.GetWorkerEnrollment(enrollment.WorkerId)!;
        Assert.NotNull(replaced.ContainerRef);
        Assert.NotEqual(originalContainerRef, replaced.ContainerRef);
        // The runtime binding points at the replaced container reference too, while
        // its session_ref stays the authoritative ACP session.
        var bindingRef = fixture.Raw($"SELECT container_ref FROM runtime_bindings WHERE id='{enrollment.RuntimeBindingId}';");
        Assert.Equal(replaced.ContainerRef, bindingRef);
    }

    [Fact]
    public async Task ReplaceConvergesWhenTheContainerIsAbsentWithoutRemovingBlindly()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        // Simulate a crash after removal: the owned container is gone.
        fixture.Provisioner.SimulateAbsentContainer(enrollment.ContainerName);
        var before = fixture.Provisioner.Effects.Count;

        var result = await fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None);

        var effects = fixture.Provisioner.Effects.Skip(before).ToArray();
        Assert.DoesNotContain(effects, x => x.StartsWith("remove-container:", StringComparison.Ordinal));
        Assert.DoesNotContain(effects, x => x.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Contains("container:" + enrollment.ContainerName, effects);
        Assert.Contains("start:" + enrollment.ContainerName, effects);
        Assert.Equal(fixture.NativeSessionId, result.NativeSessionId);
    }

    [Fact]
    public async Task ReplaceStartsAStoppedOwnedContainerInsteadOfRecreatingIt()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        fixture.Provisioner.SetState(enrollment.ContainerName, "stopped");
        var before = fixture.Provisioner.Effects.Count;

        await fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None);

        var effects = fixture.Provisioner.Effects.Skip(before).ToArray();
        Assert.Contains("start:" + enrollment.ContainerName, effects);
        Assert.DoesNotContain(effects, x => x.StartsWith("container:", StringComparison.Ordinal));
        Assert.DoesNotContain(effects, x => x.StartsWith("remove-container:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceRejectsAForeignContainerAtTheName()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        fixture.Provisioner.SetState(enrollment.ContainerName, "running");
        fixture.Provisioner.OwnerOverride = "another-owner";

        var failure = await Assert.ThrowsAsync<ForeignResourceException>(() =>
            fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Contains("not exactly owned", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceRecoversAfterAFailureBeforeAndAfterCreateOnRepeat()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();

        // Fail the first create. The owned container was removed, so the name is
        // free; the repeat must create it and converge without a duplicate.
        var baseline = fixture.Provisioner.Effects.Count;
        fixture.Provisioner.FailCreateContainerOnce = true;
        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() =>
            fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None));

        var result = await fixture.Provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, CancellationToken.None);
        Assert.Equal(fixture.NativeSessionId, result.NativeSessionId);
        Assert.Equal(2, fixture.Provisioner.Effects.Skip(baseline).Count(x => x == "container:" + enrollment.ContainerName));
    }

    [Fact]
    public async Task CoordinatorDrivesApprovedHireToReadyThroughDeliveryReplacementAndComprehension()
    {
        using var fixture = new Fixture();
        await fixture.ApproveManagedHireAsync();

        var result = await fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None);

        Assert.Equal(HireRequestStates.Ready, result.Hire.State);
        Assert.Equal(fixture.EmployeeId, result.Employee.EmployeeId);
        Assert.Equal("enrolled", result.Enrollment.LifecycleStatus);
        Assert.Equal(OrientationStates.Comprehended, result.Orientation.State);
        Assert.True(result.Orientation.Ready);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.Orientation.EvidenceSource);
        Assert.False(result.Orientation.RestartRequired);
        Assert.NotNull(result.Orientation.LoadedRuntimeGeneration);
        Assert.True(result.Orientation.LoadedRuntimeGeneration >= result.Orientation.RequiredRuntimeGeneration);

        // Exactly one employee and one enrollment were created; the original
        // provisioned container plus the deliberate replacement container exist.
        Assert.Single(fixture.Store.ListWorkerEnrollments());
        Assert.Equal(2, fixture.Provisioner.Containers.Count);
        // The install was delivered and the fresh process loaded the session.
        Assert.Contains(fixture.OrientationSession.Invocations, x => x.Operation == "install-orientation");
        Assert.Contains(fixture.OrientationSession.Invocations, x => x.Operation == "orientation-comprehension");
        Assert.Contains(fixture.VerificationSession.Invocations, x => x.Operation == "load-session");
    }

    [Fact]
    public async Task CoordinatorRepeatsIdempotentlyOnceTheHireIsReady()
    {
        using var fixture = new Fixture();
        await fixture.ApproveManagedHireAsync();
        var first = await fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None);
        var containerCount = fixture.Provisioner.Containers.Count;
        var installCount = fixture.OrientationSession.Invocations.Count(x => x.Operation == "install-orientation");
        var comprehensionCount = fixture.OrientationSession.Invocations.Count(x => x.Operation == "orientation-comprehension");

        var second = await fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None);

        Assert.Equal(HireRequestStates.Ready, second.Hire.State);
        Assert.Equal(first.Orientation.AssignmentId, second.Orientation.AssignmentId);
        Assert.Equal(containerCount, fixture.Provisioner.Containers.Count);
        Assert.Equal(installCount, fixture.OrientationSession.Invocations.Count(x => x.Operation == "install-orientation"));
        // A Ready hire never re-prompts the model.
        Assert.Equal(comprehensionCount, fixture.OrientationSession.Invocations.Count(x => x.Operation == "orientation-comprehension"));
        Assert.Single(fixture.Store.ListWorkerEnrollments());
    }

    [Fact]
    public async Task CoordinatorResumesAfterRestartWithoutASecondEmployeeEnrollmentOrContainer()
    {
        using var fixture = new Fixture();
        await fixture.ApproveManagedHireAsync();

        // A partial run: plan and apply happened, orientation was delivered, but the
        // process died before the replacement. Seed that exact state plus the
        // partial remote effects, then resume with the same method the endpoint and
        // hosted service call.
        var creation = fixture.Store.CreateManagedEmployeeFromHire(fixture.HireId);
        var enrollment = await fixture.Provisioning.PlanManagedAsync(creation.RuntimeBindingId, CancellationToken.None);
        enrollment = await fixture.Provisioning.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);
        var artifact = fixture.Store.ComposeAndAssignCurrentOrientation(creation.EmployeeId);
        _ = await fixture.Orientation.DeliverAsync(enrollment, artifact, CancellationToken.None);
        fixture.Store.TransitionHireRequestState(fixture.HireId, fixture.Store.GetHireRequest(fixture.HireId)!.Revision, HireRequestStates.Approved, HireRequestStates.Provisioning, null);
        fixture.Store.TransitionHireRequestState(fixture.HireId, fixture.Store.GetHireRequest(fixture.HireId)!.Revision, HireRequestStates.Provisioning, HireRequestStates.Orienting, null);
        var containerCount = fixture.Provisioner.Containers.Count;

        var result = await fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None);

        Assert.Equal(HireRequestStates.Ready, result.Hire.State);
        Assert.Single(fixture.Store.ListWorkerEnrollments());
        Assert.Single(fixture.Store.GetOverview().Employees, x => x.RuntimeBindingId == creation.RuntimeBindingId);
        // The resume completed the replacement; it never re-provisioned the volumes.
        Assert.Equal(containerCount + 1, fixture.Provisioner.Containers.Count);
        Assert.Equal(fixture.EmployeeId, result.Employee.EmployeeId);
        Assert.Equal(fixture.NativeSessionId, result.Orientation.SessionId);
    }

    [Fact]
    public async Task CoordinatorRecordsUncertainWhenTheWorkerMutationOutcomeIsUnknown()
    {
        using var fixture = new Fixture();
        await fixture.ApproveManagedHireAsync();
        fixture.OrientationSession.FailInstallAsWriteUncertain = true;

        await Assert.ThrowsAsync<WorkerWriteUncertainException>(() =>
            fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None));

        var hire = fixture.Store.GetHireRequest(fixture.HireId)!;
        Assert.Equal(HireRequestStates.Uncertain, hire.State);
        Assert.NotNull(hire.StatusDetail);
    }

    [Fact]
    public async Task CoordinatorRecordsFailedOnDeterministicConfigurationFailure()
    {
        using var fixture = new Fixture();
        await fixture.ApproveManagedHireAsync();
        // A global ceiling below the frozen managed memory makes the plan a
        // deterministic configuration failure.
        fixture.LowerMemoryCeiling();

        await Assert.ThrowsAsync<WorkerControlConfigurationException>(() =>
            fixture.Coordinator.ProvisionToOrientingAsync(fixture.HireId, CancellationToken.None));

        Assert.Equal(HireRequestStates.Failed, fixture.Store.GetHireRequest(fixture.HireId)!.State);
    }

    [Fact]
    public async Task EmployeeRebuildUsesNewVerifiedDigestPreservesVolumesAndSessionAndClearsHold()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "rebuild");
        var effectsBefore = fixture.Provisioner.Effects.Count;

        var result = await fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, target.Id, false, false, null, CancellationToken.None);

        Assert.Equal(EmployeeRebuildStates.Applied, result.State);
        Assert.NotNull(result.EvidenceHash);
        Assert.True(result.OwnershipEpochAfter > result.OwnershipEpochBefore);
        var created = fixture.Provisioner.Containers.Last();
        Assert.Equal(RebuildDigest, created.ImageDigest);
        Assert.Equal(target.Id, created.Identity.ProfileRevisionId);
        Assert.Equal([enrollment.ControlVolumeName, enrollment.HomeVolumeName, enrollment.WorkspaceVolumeName, enrollment.SessionVolumeName], created.Volumes.Select(x => x.Name).ToArray());
        Assert.DoesNotContain(fixture.Provisioner.Effects.Skip(effectsBefore), x => x.StartsWith("remove-volume:", StringComparison.Ordinal));
        Assert.Equal(fixture.NativeSessionId, fixture.Process.LoadedSessionId);
        Assert.False(fixture.ManualHoldActive());
        Assert.Equal(BuiltDigest, fixture.Store.GetWorkerEnrollment(enrollment.WorkerId)!.ExpectedImageDigest);
        Assert.Equal(RebuildDigest, fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!.CurrentImageDigest);
    }

    [Theory]
    [InlineData(true, false, OrganizationStore.EmployeeRebuildResetConfirmation.Workspace)]
    [InlineData(false, true, OrganizationStore.EmployeeRebuildResetConfirmation.Home)]
    public async Task EmployeeRebuildResetsOnlyTheConfirmedVolume(bool resetWorkspace, bool resetHome, string confirmation)
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "reset");
        var before = fixture.Provisioner.Effects.Count;

        var result = await fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, target.Id, resetWorkspace, resetHome, confirmation, CancellationToken.None);

        Assert.Equal(EmployeeRebuildStates.Applied, result.State);
        var effects = fixture.Provisioner.Effects.Skip(before).ToArray();
        var expected = resetWorkspace ? enrollment.WorkspaceVolumeName : enrollment.HomeVolumeName;
        var untouched = resetWorkspace ? enrollment.HomeVolumeName : enrollment.WorkspaceVolumeName;
        Assert.Contains("remove-volume:" + expected, effects);
        Assert.Contains("volume:" + expected, effects);
        Assert.DoesNotContain("remove-volume:" + untouched, effects);
        Assert.DoesNotContain("remove-volume:" + enrollment.ControlVolumeName, effects);
        Assert.DoesNotContain("remove-volume:" + enrollment.SessionVolumeName, effects);
    }

    [Fact]
    public async Task EmployeeRebuildRefusesMissingResetConfirmationBeforeRemoteEffects()
    {
        using var fixture = new Fixture();
        await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "confirmation");
        var before = fixture.Provisioner.Effects.Count;

        await Assert.ThrowsAsync<OrganizationValidationException>(() =>
            fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, target.Id, true, false, null, CancellationToken.None));

        Assert.Equal(before, fixture.Provisioner.Effects.Count);
        Assert.Empty(fixture.Store.ListEmployeeRebuilds());
    }

    [Fact]
    public async Task EmployeeRebuildRefusesSameRevisionAndCrossProfileWithoutRemoteEffects()
    {
        using var fixture = new Fixture();
        await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var before = fixture.Provisioner.Effects.Count;
        var current = fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!;

        await Assert.ThrowsAsync<OrganizationValidationException>(() =>
            fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, current.CurrentProfileRevisionId, false, false, null, CancellationToken.None));

        var other = fixture.CreateOtherProfileVerifiedRevision(RebuildDigest);
        await Assert.ThrowsAsync<OrganizationValidationException>(() =>
            fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, other.Id, false, false, null, CancellationToken.None));
        Assert.Equal(before, fixture.Provisioner.Effects.Count);
    }

    [Fact]
    public async Task EmployeeRebuildForeignContainerFailsClosedAndRetainsHoldAsUncertain()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "foreign");
        fixture.Provisioner.OwnerOverride = "another-owner";

        await Assert.ThrowsAsync<ForeignResourceException>(() =>
            fixture.RebuildCoordinator.RebuildAsync(fixture.EmployeeId, target.Id, false, false, null, CancellationToken.None));

        var rebuild = Assert.Single(fixture.Store.ListEmployeeRebuilds(enrollment.WorkerId));
        Assert.Equal(EmployeeRebuildStates.Uncertain, rebuild.State);
        Assert.True(fixture.ManualHoldActive());
        Assert.DoesNotContain(fixture.Provisioner.Effects, x => x == "remove-container:" + enrollment.ContainerName);
    }

    [Fact]
    public async Task EmployeeRebuildStartupReconciliationAppliesRunningTargetOrLeavesAbsenceUncertain()
    {
        using var fixture = new Fixture();
        var enrollment = await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "restart");
        var intent = fixture.BeginRebuild(target.Id, RebuildDigest);
        fixture.Store.SetManualDispatchHold(fixture.EmployeeId, true, "rebuild " + intent.Id);
        var holding = fixture.Store.TransitionEmployeeRebuild(intent.Id, intent.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
        var replacing = fixture.Store.TransitionEmployeeRebuild(intent.Id, holding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
        await fixture.Provisioning.ReplaceContainerAsync(enrollment.WorkerId, new(RebuildDigest, "linux/amd64", target.Id), CancellationToken.None);

        var reconciled = await fixture.RebuildCoordinator.ReconcileInterruptedOnStartup();
        Assert.Contains(replacing.Id, reconciled);
        Assert.Equal(EmployeeRebuildStates.Applied, fixture.Store.GetEmployeeRebuild(replacing.Id)!.State);
        Assert.False(fixture.ManualHoldActive());

        using var absentFixture = new Fixture();
        var absentEnrollment = await absentFixture.EnrollAsync();
        await absentFixture.MakeOrientationReadyAsync();
        var absentTarget = absentFixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "absent");
        var absent = absentFixture.BeginRebuild(absentTarget.Id, RebuildDigest);
        absentFixture.Store.SetManualDispatchHold(absentFixture.EmployeeId, true, "rebuild " + absent.Id);
        var absentHolding = absentFixture.Store.TransitionEmployeeRebuild(absent.Id, absent.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
        absentFixture.Store.TransitionEmployeeRebuild(absent.Id, absentHolding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
        absentFixture.Provisioner.SimulateAbsentContainer(absentEnrollment.ContainerName);

        await absentFixture.RebuildCoordinator.ReconcileInterruptedOnStartup();
        Assert.Equal(EmployeeRebuildStates.Uncertain, absentFixture.Store.GetEmployeeRebuild(absent.Id)!.State);
        Assert.True(absentFixture.ManualHoldActive());
    }

    [Fact]
    public async Task EmployeeRebuildResumesAStrandedIntentAndABandonRestoresRemediation()
    {
        using var fixture = new Fixture();
        await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "resume");

        // A stranded Intent (crash between commit and hold) is driven to Applied by
        // ResumeAsync, which takes the hold before doing any work.
        var intent = fixture.BeginRebuild(target.Id, RebuildDigest);
        Assert.Equal(EmployeeRebuildStates.Intent, intent.State);
        var resumed = await fixture.RebuildCoordinator.ResumeAsync(intent.Id, CancellationToken.None);
        Assert.Equal(EmployeeRebuildStates.Applied, resumed.Rebuild.State);
        Assert.False(fixture.ManualHoldActive());
    }

    [Fact]
    public async Task EmployeeRebuildAbandonRestoresAnOwnersPreexistingHold()
    {
        using var fixture = new Fixture();
        await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "abandon");

        // The owner already held dispatch before the rebuild. The coordinator
        // captures that when it begins and restores it on abandon rather than
        // clearing a hold it did not take.
        fixture.Store.SetManualDispatchHold(fixture.EmployeeId, true, "owner inspection");
        var intent = fixture.BeginRebuild(target.Id, RebuildDigest, holdPreexisting: true);
        Assert.True(fixture.Store.GetEmployeeRebuild(intent.Id)!.HoldPreexisting);
        var abandoned = fixture.RebuildCoordinator.AbandonAsync(intent.Id, "owner-abandoned-rebuild");
        Assert.Equal(EmployeeRebuildStates.Failed, abandoned.State);
        Assert.True(fixture.ManualHoldActive());

        // A rebuild with no preexisting hold clears cleanly and copies the flag false.
        using var clean = new Fixture();
        await clean.EnrollAsync();
        await clean.MakeOrientationReadyAsync();
        var cleanTarget = clean.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "abandon-clean");
        var cleanIntent = clean.BeginRebuild(cleanTarget.Id, RebuildDigest);
        Assert.False(clean.Store.GetEmployeeRebuild(cleanIntent.Id)!.HoldPreexisting);
        _ = clean.RebuildCoordinator.AbandonAsync(cleanIntent.Id, "owner-abandoned-rebuild");
        Assert.False(clean.ManualHoldActive());
    }

    [Fact]
    public async Task EmployeeRebuildAbandonIsRefusedWhileAReplacementMayBeInFlight()
    {
        using var fixture = new Fixture();
        await fixture.EnrollAsync();
        await fixture.MakeOrientationReadyAsync();
        var target = fixture.CreateVerifiedRevision(RebuildDigest, "linux/amd64", "abandon-mid");

        // Move the durable row into Replacing without driving the remote swap, so
        // the state is exactly the mid-replacement window an abandon must not touch.
        var intent = fixture.BeginRebuild(target.Id, RebuildDigest);
        fixture.Store.SetManualDispatchHold(fixture.EmployeeId, true, "rebuild " + intent.Id);
        var holding = fixture.Store.TransitionEmployeeRebuild(intent.Id, intent.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
        var replacing = fixture.Store.TransitionEmployeeRebuild(intent.Id, holding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => Task.Run(() => fixture.RebuildCoordinator.AbandonAsync(replacing.Id, "owner-abandoned-rebuild")));

        // The row and its hold are untouched: the rebuild must be resumed to a
        // durable outcome instead of abandoned mid-swap.
        Assert.Equal(EmployeeRebuildStates.Replacing, fixture.Store.GetEmployeeRebuild(replacing.Id)!.State);
        Assert.True(fixture.ManualHoldActive());
    }

    // ---------------------------------------------------------------- fixture

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string _databasePath;
        private readonly AcpControlHost _control;

        public Fixture()
        {
            _databasePath = Path.Combine(_temp.Path, "control.db");
            Store = new OrganizationStore(_databasePath, lockTimeout: TimeSpan.FromSeconds(5));
            Store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            PrepareHost();
            BuildProfile();
            Provisioner = new ReplacementProvisioner();
            Process = new FakeWorkerProcess(NativeSessionId);
            Provisioner.Process = Process;
            OrientationSession = new FakeSession(Process, isFreshOnConnect: false);
            VerificationSession = new FakeSession(Process, isFreshOnConnect: true);
            _control = BuildControl();
            Provisioning = new RemoteWorkerProvisioningCoordinator(_control, Provisioner, Microsoft.Extensions.Options.Options.Create(BuildOptions()), new FixedFactory(VerificationSession));
            Orientation = new RemoteOrientationCoordinator(new FixedFactory(OrientationSession));
        }

        public OrganizationStore Store { get; }
        public ReplacementProvisioner Provisioner { get; }
        public FakeWorkerProcess Process { get; }
        public FakeSession OrientationSession { get; }
        public FakeSession VerificationSession { get; }
        public RemoteWorkerProvisioningCoordinator Provisioning { get; }
        public RemoteOrientationCoordinator Orientation { get; }
        public HireProvisioningCoordinator Coordinator => new(_control, Provisioning, Orientation, Microsoft.Extensions.Options.Options.Create(BuildOptions()), NullLogger<HireProvisioningCoordinator>.Instance);
        public EmployeeRebuildCoordinator RebuildCoordinator => new(_control, Provisioning, new FixedFactory(VerificationSession), Microsoft.Extensions.Options.Options.Create(BuildOptions()), NullLogger<EmployeeRebuildCoordinator>.Instance);
        public string NativeSessionId { get; } = "native-managed";
        public string HireId { get; private set; } = string.Empty;
        public string EmployeeId { get; private set; } = string.Empty;

        private WorkerControlOptions? _options;

        public WorkerControlOptions BuildOptions()
        {
            if (_options is not null) return _options;
            var known = Path.Combine(_temp.Path, "known_hosts");
            var identity = Path.Combine(_temp.Path, "id_ed25519");
            File.WriteAllText(known, "host-a.example ssh-ed25519 " + Convert.ToBase64String(new byte[32]) + "\n");
            File.WriteAllBytes(identity, new byte[32]);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(known, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return _options = new WorkerControlOptions
            {
                Enabled = true,
                ControllerId = "controller-a",
                ApprovedImageDigest = BaseDigest,
                ApprovedImagePlatform = "linux/amd64",
                ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "host-a.example", Port = 22, Username = "roys", KnownHostsPath = known, IdentityFilePath = identity }],
                ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
                MemoryBytes = 2L * 1024 * 1024 * 1024,
                CpuLimit = 2,
                PidsLimit = 256,
            };
        }

        public void LowerMemoryCeiling() => BuildOptions().MemoryBytes = 128L * 1024 * 1024;

        public async Task<WorkerEnrollmentRecord> EnrollAsync()
        {
            await ApproveManagedHireAsync();
            var enrollment = await Provisioning.PlanManagedAsync(BindingId, CancellationToken.None);
            return await Provisioning.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);
        }

        public string BindingId => Store.GetHireRequestApproval(HireId) is { RuntimeBindingId: { } binding }
            ? binding
            : Store.ListWorkerEnrollments().Single().RuntimeBindingId;

        public async Task ApproveManagedHireAsync()
        {
            var profile = Store.ListContainerProfiles().Single();
            var revisionId = Store.GetContainerProfile(profile.Id)!.Revisions.Single().Id;
            var overview = Store.GetOverview();
            var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
            var role = Assert.Single(overview.Roles);
            var hire = Store.CreateHireRequest(new HireRequestCreate(
                Guid.NewGuid().ToString("N"), "Managed Dev", "Managed hire under test.", department.Id, role.Id,
                RuntimePlacements.DeveloperContainer, 1, 1024, 128), null);
            HireId = hire.Id;
            Store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revisionId), "owner");
            EmployeeId = Store.CreateManagedEmployeeFromHire(hire.Id).EmployeeId;
            OrientationSession.Comprehension = BuildComprehension;
        }

        public async Task MakeOrientationReadyAsync()
        {
            try
            {
                if (Store.GetOrientationStatus(EmployeeId).Ready) return;
            }
            catch (OrganizationNotFoundException)
            {
                // The focused rebuild fixture has provisioned the worker but has
                // not run the hire coordinator's orientation phase yet.
            }
            var artifact = Store.ComposeAndAssignCurrentOrientation(EmployeeId);
            var sessionId = artifact.SessionId ?? throw new InvalidOperationException("Fixture orientation has no native session.");
            Store.MarkOrientationDelivered(artifact.AssignmentId, artifact.OrientationVersion, sessionId, artifact.AssignmentRevision, Process.ProcessGeneration);
            Store.ConfirmOrientationLoaded(EmployeeId, artifact.AssignmentId, artifact.OrientationVersion, sessionId, Process.ProcessGeneration);
            var record = BuildComprehension(artifact.AssignmentId);
            var statusAfterLoad = Store.GetOrientationStatus(EmployeeId);
            Store.ValidateAndRecordComprehension(new OrientationEvidenceRequest(
                record.AssignmentId, record.EmployeeId, record.SessionId, record.OrientationVersion,
                record.Identity, record.Department, record.Reporting, record.Duties, record.Restrictions, record.Escalation,
                statusAfterLoad.Revision), OrientationEvidenceSource.LiveModel);
            await Task.CompletedTask;
        }

        public ContainerProfileRevisionSummary CreateVerifiedRevision(string digest, string platform, string suffix)
        {
            var profile = Store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
            var revision = Store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
                Store.GetContainerProfile(profile.Id)!.Profile.Revision,
                JsonSerializer.Serialize(new { image = "agentcontrol-worker-base", name = suffix, containerEnv = new Dictionary<string, string> { ["EDITOR"] = suffix } }),
                null));
            BuildVerified(revision.Id, digest, platform, "agentcontrol-profile:" + suffix);
            return revision;
        }

        public ContainerProfileRevisionSummary CreateOtherProfileVerifiedRevision(string digest)
        {
            var profile = Store.CreateContainerProfile(new ContainerProfileCreate(Guid.NewGuid().ToString("N"), "other-profile", "Other profile", "Cross-profile test.", """{"image":"agentcontrol-worker-base","name":"Other"}""", null), null);
            var revision = Store.GetContainerProfile(profile.Id)!.Revisions.Single();
            BuildVerified(revision.Id, digest, "linux/amd64", "agentcontrol-profile:other");
            return revision;
        }

        public EmployeeRebuildRecord BeginRebuild(string targetRevisionId, string targetDigest, bool holdPreexisting = false)
        {
            var status = Store.GetEmployeeProfileStatus(EmployeeId)!;
            var enrollment = Store.GetWorkerEnrollment(status.WorkerId!)!;
            var build = Store.GetVerifiedProfileBuild(targetRevisionId, enrollment.HostId)!;
            return Store.BeginEmployeeRebuild(new(
                EmployeeId, status.RuntimeBindingId, enrollment.WorkerId, enrollment.HostId,
                status.CurrentProfileRevisionId, status.CurrentImageDigest, status.CurrentRevisionNumber,
                targetRevisionId, build.Id, targetDigest, build.Platform, false, false, null, enrollment.OwnershipEpoch, holdPreexisting));
        }

        public bool ManualHoldActive() => Raw("SELECT CAST(COUNT(*) AS TEXT) FROM dispatch_holds WHERE runtime_binding_id='" + BindingId + "' AND reason='manual' AND active=1;") != "0";

        private ProfileBuildRecord BuildVerified(string revisionId, string digest, string platform, string tag)
        {
            var queued = Store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, platform, ContextHash, tag);
            var building = Store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            var verifying = Store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
            return Store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: digest, verified: true, evidenceHash: ContextHash);
        }

        /// <summary>
        /// Answers the comprehension operation with the employee's exact persisted
        /// orientation facts, so the store's own validation decides readiness rather
        /// than the fake asserting it.
        /// </summary>
        private OrientationComprehensionRecord BuildComprehension(string assignmentId)
        {
            var status = Store.GetOrientationStatus(EmployeeId);
            var facts = ReadFacts(assignmentId);
            return new OrientationComprehensionRecord(
                assignmentId,
                EmployeeId,
                status.SessionId!,
                status.OrientationVersion,
                facts["identity"].Single(),
                facts["department"].Single(),
                facts["reporting"].Single(),
                facts["duty"],
                facts["restriction"],
                facts["escalation"].Single(),
                "comprehended",
                "sha256:" + new string('a', 64),
                false);
        }

        private Dictionary<string, List<string>> ReadFacts(string assignmentId)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f.category, f.value
                FROM orientation_assignment_fragments af
                JOIN orientation_facts f ON f.fragment_id = af.fragment_id
                WHERE af.assignment_id = $assignment
                ORDER BY af.ordinal, f.category, f.ordinal
                """;
            command.Parameters.AddWithValue("$assignment", assignmentId);
            var facts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var category = reader.GetString(0);
                if (!facts.TryGetValue(category, out var values)) facts[category] = values = [];
                values.Add(reader.GetString(1));
            }

            return facts;
        }

        public string Raw(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private void PrepareHost()
        {
            // The SSH host stays registered for the manual-path tests, but managed
            // hires approve only against the controller-local Docker target.
            Store.RegisterExecutionHost("host-a", "host-a.example", 22, "roys", "/known", new ExecutionHostRegistration("host-a", "host-a", "Host A"));
            var host = Store.GetExecutionHost("host-a")!;
            Store.RecordExecutionHostProbe("host-a", host.Revision, new ExecutionHostProbe(
                "ssh-ed25519", "SHA256:x", "sha256:" + new string('1', 64), "29.0", "1.51", "x86_64", "overlay2", "ext4",
                false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));

            var local = Store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
            Store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, local.Revision, new LocalExecutionHostProbe(
                "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));
        }

        private void BuildProfile()
        {
            var profile = Store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
            var revisionId = Store.GetContainerProfile(profile.Id)!.Revisions.Single().Id;
            var queued = Store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:x");
            var building = Store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            var verifying = Store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
            Store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: BuiltDigest, verified: true, evidenceHash: ContextHash);
        }

        private AcpControlHost BuildControl()
        {
            var options = new ControlOptions { DataDirectory = _temp.Path, PrivateDataDirectory = Path.Combine(_temp.Path, "private") };
            var control = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
            typeof(AcpControlHost).GetField("_organization", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, Store);
            return control;
        }

        public void Dispose() { Store.Dispose(); _temp.Dispose(); }
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FixedFactory(IWorkerBridgeSession session) : IWorkerBridgeSessionFactory
    {
        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    /// <summary>
    /// Models one worker process whose in-memory ACP session can be cleared to
    /// represent a fresh process, and whose persisted orientation-installed fields
    /// survive a restart because they live on the home volume.
    /// </summary>
    private sealed class FakeWorkerProcess(string nativeSessionId)
    {
        public string NativeSessionId { get; } = nativeSessionId;
        public long ProcessGeneration { get; private set; } = 1;
        public string? LoadedSessionId { get; set; }
        public string? OrientationAssignmentId { get; private set; }
        public string? OrientationVersion { get; private set; }
        public string? OrientationArtifactFileName { get; private set; }
        public string? OrientationContentHash { get; private set; }
        public string? OrientationState { get; private set; }
        public string? OrientationInstalledPath { get; private set; }

        public void RestartGeneration()
        {
            ProcessGeneration++;
            LoadedSessionId = null;
        }

        public void RecordInstall(string assignmentId, string version, string fileName, string contentHash, string installedPath)
        {
            OrientationAssignmentId = assignmentId;
            OrientationVersion = version;
            OrientationArtifactFileName = fileName;
            OrientationContentHash = contentHash;
            OrientationState = "installed";
            OrientationInstalledPath = installedPath;
        }

        public BridgeWorkerStatus Status() => new(
            WorkerGeneration: 1,
            ProcessGeneration: ProcessGeneration,
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
            SessionId: LoadedSessionId,
            SessionOperationState: "none",
            SessionOperationRequestId: null,
            OrientationAssignmentId: OrientationAssignmentId,
            OrientationVersion: OrientationVersion,
            OrientationArtifactFileName: OrientationArtifactFileName,
            OrientationContentHash: OrientationContentHash,
            OrientationState: OrientationState,
            OrientationInstalledPath: OrientationInstalledPath);
    }

    private sealed class FakeSession(FakeWorkerProcess process, bool isFreshOnConnect) : IWorkerBridgeSession
    {
        public WorkerBridgeLease Lease { get; } = new(1, "controller-a", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
        public List<(string Operation, string Payload)> Invocations { get; } = [];
        public bool FailInstallAsWriteUncertain { get; set; }

        /// <summary>
        /// The exact persisted facts the managed employee is oriented on. The fake
        /// worker answers comprehension with these so the store's exact validation
        /// is what decides readiness, exactly as a live model answer would be judged.
        /// </summary>
        public Func<string, OrientationComprehensionRecord>? Comprehension { get; set; }
        private bool _firstConnect = true;

        public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) =>
            new Dictionary<string, object?>(fields, StringComparer.Ordinal) { ["operation"] = operation };

        public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(request, WorkerProtocol.JsonOptions);
            Invocations.Add((operation, payload));
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            object value = operation switch
            {
                "status" => StatusPayload(),
                "new-session" => NewSession(),
                "load-session" => LoadSession(root),
                "install-orientation" => Install(root),
                "orientation-comprehension" => Comprehend(root),
                _ => new { ok = true },
            };
            using var result = JsonDocument.Parse(JsonSerializer.Serialize(value, WorkerProtocol.JsonOptions));
            return Task.FromResult(new WorkerSessionResult(operation, result.RootElement.Clone(), mutation));
        }

        private BridgeWorkerStatus StatusPayload()
        {
            if (isFreshOnConnect && _firstConnect)
            {
                _firstConnect = false;
                process.LoadedSessionId = null;
            }
            return process.Status();
        }

        private string NewSession()
        {
            process.LoadedSessionId = process.NativeSessionId;
            return process.NativeSessionId;
        }

        private object LoadSession(JsonElement root)
        {
            process.LoadedSessionId = root.GetProperty("sessionId").GetString();
            return new { ok = true };
        }

        private OrientationInstallRecord Install(JsonElement root)
        {
            if (FailInstallAsWriteUncertain) throw new WorkerWriteUncertainException("injected uncertain orientation install");
            var assignmentId = root.GetProperty("assignmentId").GetString()!;
            var version = root.GetProperty("orientationVersion").GetString()!;
            var fileName = root.GetProperty("artifactFileName").GetString()!;
            var contentHash = root.GetProperty("contentHash").GetString()!;
            var path = WorkerProtocol.OrientationRootDirectory + "/" + fileName;
            process.RecordInstall(assignmentId, version, fileName, contentHash, path);
            return new OrientationInstallRecord(assignmentId, version, fileName, contentHash, "installed", path, false);
        }

        private OrientationComprehensionRecord Comprehend(JsonElement root)
        {
            var assignmentId = root.GetProperty("assignmentId").GetString()!;
            if (Comprehension is null) throw new InvalidOperationException("No comprehension evidence was configured.");
            return Comprehension(assignmentId);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReplacementProvisioner : IRemoteWorkerProvisioner
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
        private int _containerAttempts;
        private int _containerRefs;

        public FakeWorkerProcess Process { get; set; } = null!;
        public List<string> Effects { get; } = [];
        public List<ContainerCreateSpec> Containers { get; } = [];
        public bool FailCreateContainerOnce { get; set; }
        public string? OwnerOverride { get; set; }

        public void SetState(string name, string state) => _states[name] = state;

        /// <summary>Removes the tracked container without recording a remote effect, modelling a crash after removal.</summary>
        public void SimulateAbsentContainer(string name) { _labels.Remove(name); _states.Remove(name); }

        public Task<HostProbePayload> ProbeAsync(ExecutionTarget host, CancellationToken token) => throw new NotSupportedException();

        public Task<string> CreateVolumeAsync(ExecutionTarget host, VolumeCreateSpec spec, CancellationToken token)
        {
            Effects.Add("volume:" + spec.Name);
            _labels[spec.Name] = spec.Identity.Labels;
            return Task.FromResult("volume-ref-" + spec.Name);
        }

        public Task<string> CreateContainerAsync(ExecutionTarget host, ContainerCreateSpec spec, CancellationToken token)
        {
            Effects.Add("container:" + spec.Name);
            if (FailCreateContainerOnce && Interlocked.Increment(ref _containerAttempts) == 1) throw new RemoteWorkerUnavailableException("injected create failure", transport: true);
            _labels[spec.Name] = spec.Identity.Labels;
            _states[spec.Name] = "running";
            Containers.Add(spec);
            // Creating the replacement container starts a fresh worker process: the
            // in-memory session is gone until it is loaded again.
            Process.RestartGeneration();
            return Task.FromResult("container-ref-" + spec.Name + "-" + Interlocked.Increment(ref _containerRefs));
        }

        public Task BootstrapAsync(ExecutionTarget host, BootstrapSpec spec, byte[] key, CancellationToken token)
        {
            Effects.Add("bootstrap:" + spec.ControlVolumeName);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            return Task.CompletedTask;
        }

        public Task StartAsync(ExecutionTarget host, string container, CancellationToken token)
        {
            Effects.Add("start:" + container);
            if (_labels.ContainsKey(container)) _states[container] = "running";
            return Task.CompletedTask;
        }

        public Task StopAsync(ExecutionTarget host, string container, CancellationToken token)
        {
            Effects.Add("stop:" + container);
            if (_labels.ContainsKey(container)) _states[container] = "stopped";
            return Task.CompletedTask;
        }

        public Task RemoveContainerAsync(ExecutionTarget host, string container, CancellationToken token)
        {
            Effects.Add("remove-container:" + container);
            _labels.Remove(container);
            _states.Remove(container);
            return Task.CompletedTask;
        }

        public Task RemoveVolumeAsync(ExecutionTarget host, string volume, CancellationToken token)
        {
            Effects.Add("remove-volume:" + volume);
            _labels.Remove(volume);
            return Task.CompletedTask;
        }

        public Task RemoveImageAsync(ExecutionTarget host, string imageReference, CancellationToken token)
        {
            Effects.Add("remove-image:" + imageReference);
            return Task.CompletedTask;
        }

        public Task<string?> InspectImageAsync(ExecutionTarget host, string imageReference, CancellationToken token) =>
            Task.FromResult(imageReference.StartsWith("sha256:", StringComparison.Ordinal) ? imageReference : null);

        public Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget host, string name, CancellationToken token) => Inspect(name, "present");
        public Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget host, string name, CancellationToken token) => Inspect(name, _states.TryGetValue(name, out var state) ? state : "running");

        private Task<RemoteResourceInspection> Inspect(string name, string state)
        {
            if (!_labels.TryGetValue(name, out var labels)) return Task.FromResult(new RemoteResourceInspection(false, null, new Dictionary<string, string>(), "absent"));
            var effective = new Dictionary<string, string>(labels, StringComparer.Ordinal) { ["org.opencontainers.image.revision"] = "inherited-image-label" };
            if (OwnerOverride is not null) effective["agentcontrol.owner"] = OwnerOverride;
            return Task.FromResult(new RemoteResourceInspection(true, "ref-" + name, effective, state));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hvo-hire-provisioning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, (UnixFileMode)0x1C0);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
