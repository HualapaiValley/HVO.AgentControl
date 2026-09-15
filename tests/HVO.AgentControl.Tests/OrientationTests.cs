using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class OrientationTests
{
    [Fact]
    public void CompositionIsDeterministicOrderedAndContainsNoTaskText()
    {
        var fragments = new[]
        {
            new OrientationFragment("frag-org", "organization", "org", 1, "org\r\ntext"),
            new OrientationFragment("frag-dept", "department", "dept", 1, "dept text"),
            new OrientationFragment("frag-role", "role", "role", 2, "role text"),
            new OrientationFragment("frag-employee", "employee", "employee", 3, "employee text"),
        };
        var restrictions = new[]
        {
            new OrientationRestriction("rst-z", "role", "z", "*", "z:*", "Z deny", false),
            new OrientationRestriction("rst-a", "host", "a", "*", "a:*", "A deny", false),
        };

        var first = OrientationComposer.Compose(fragments, "policy-v1", 4, "summary", restrictions);
        var second = OrientationComposer.Compose(fragments, "policy-v1", 4, "summary", restrictions.Reverse().ToArray());

        Assert.Equal(first, second);
        Assert.Equal(OrientationComposer.Hash(first), OrientationComposer.Hash(second));
        Assert.True(first.IndexOf("fragment:organization", StringComparison.Ordinal) < first.IndexOf("fragment:department", StringComparison.Ordinal));
        Assert.True(first.IndexOf("fragment:department", StringComparison.Ordinal) < first.IndexOf("fragment:role", StringComparison.Ordinal));
        Assert.True(first.IndexOf("fragment:role", StringComparison.Ordinal) < first.IndexOf("fragment:employee", StringComparison.Ordinal));
        Assert.True(first.IndexOf("A deny", StringComparison.Ordinal) < first.IndexOf("Z deny", StringComparison.Ordinal));
        Assert.Contains("Current task: none", first, StringComparison.Ordinal);
        Assert.DoesNotContain("implement issue", first, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public void SeededArtifactUsesRealLineFeedsAndContainsEveryStandingField()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        var artifact = store.ComposeAndAssignCurrentOrientation();

        Assert.DoesNotContain("\\n", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("# Organization\n\nName: AgentControl Development", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("# Department\n\n", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("# Role\n\n", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("# Employee\n\nIdentity: " + OrganizationSeed.AdoptedEmployeeDisplayName + " (" + identity.EmployeeId + ")", artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.OrganizationDescription, artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.OrganizationInstructions, artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.AdoptedEmployeePurpose, artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.AdoptedEmployeeInstructions, artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.AdoptedEmployeeRules, artifact.Content, StringComparison.Ordinal);
        Assert.Contains(OrganizationSeed.AdoptedEmployeeRestrictions, artifact.Content, StringComparison.Ordinal);
        Assert.Contains("There is no Fleet", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("No worker runtime exists yet", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("Do not perform code work", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("Prefer plain informational answers", artifact.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryComprehensionStalenessAndManualHoldAreIndependent()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-orientation", "Preserved");
        var assigned = store.ComposeAndAssignCurrentOrientation();
        Assert.False(store.CanDispatchEmployee(identity.EmployeeId));
        Assert.Equal(OrientationStates.Assigned, store.GetOrientationStatus(identity.EmployeeId).State);

        var directory = Path.Combine(root.Directory, "agent-config");
        Directory.CreateDirectory(directory);
        OrientationArtifactPublisher.Publish(directory, assigned);
        var delivered = store.MarkOrientationDelivered(assigned.AssignmentId, assigned.OrientationVersion, "ses-orientation", assigned.AssignmentRevision);
        Assert.Equal(OrientationStates.Delivered, delivered.State);
        Assert.True(delivered.DispatchHeld);

        var evidence = ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-orientation", delivered.OrientationVersion, delivered.Revision);
        var ready = store.ValidateAndRecordComprehension(evidence);
        Assert.Equal(OrientationStates.Comprehended, ready.State);
        Assert.False(ready.DispatchHeld);
        Assert.True(store.CanDispatchEmployee(identity.EmployeeId));
        Assert.NotNull(ready.EvidenceHash);
        Assert.Equal(OrientationEvidenceSources.OwnerSubmitted, ready.EvidenceSource);

        var manual = store.SetManualDispatchHold(identity.EmployeeId, true, "operator check");
        Assert.Contains(DispatchHoldReasons.Manual, manual.HoldReasons);
        Assert.False(store.CanDispatchEmployee(identity.EmployeeId));

        var beforeImageMarker = "immutable-image-digest";
        var overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(overview.Id, "Changed persisted standing instructions.", overview.Revision);
        var staleBeforeCompose = store.GetOrientationStatus(identity.EmployeeId);
        Assert.Equal(OrientationStates.Stale, staleBeforeCompose.State);
        Assert.False(staleBeforeCompose.Ready);
        Assert.False(store.CanDispatchEmployee(identity.EmployeeId));
        Assert.Contains(DispatchHoldReasons.OrientationStale, staleBeforeCompose.HoldReasons);
        Assert.Contains(DispatchHoldReasons.Manual, staleBeforeCompose.HoldReasons);
        var heldWhileStale = store.SetManualDispatchHold(identity.EmployeeId, false, null);
        Assert.Equal(OrientationStates.Stale, heldWhileStale.State);
        Assert.DoesNotContain(DispatchHoldReasons.Manual, heldWhileStale.HoldReasons);
        store.SetManualDispatchHold(identity.EmployeeId, true, "operator check");

        var changed = store.ComposeAndAssignCurrentOrientation();
        Assert.NotEqual(assigned.OrientationVersion, changed.OrientationVersion);
        Assert.Equal(identity.EmployeeId, changed.EmployeeId);
        Assert.Equal(identity.RuntimeBindingId, changed.RuntimeBindingId);
        Assert.Equal("ses-orientation", changed.SessionId);
        Assert.Equal("immutable-image-digest", beforeImageMarker);
        var stale = store.GetOrientationStatus(identity.EmployeeId);
        Assert.Contains(DispatchHoldReasons.OrientationStale, stale.HoldReasons);
        Assert.Contains(DispatchHoldReasons.Manual, stale.HoldReasons);
        Assert.False(store.CanDispatchEmployee(identity.EmployeeId));
        Assert.Throws<OrganizationConcurrencyException>(() => store.ValidateAndRecordComprehension(evidence));
    }

    [Fact]
    public void RevertingToAPriorContentVersionCreatesANewCurrentAssignment()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-revert", null);
        var original = store.ComposeAndAssignCurrentOrientation();
        store.MarkOrientationDelivered(original.AssignmentId, original.OrientationVersion, "ses-revert", original.AssignmentRevision);

        var overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(overview.Id, "Temporary B instructions.", overview.Revision);
        var changed = store.ComposeAndAssignCurrentOrientation();
        Assert.NotEqual(original.OrientationVersion, changed.OrientationVersion);

        overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(overview.Id, OrganizationSeed.OrganizationInstructions, overview.Revision);
        var reverted = store.ComposeAndAssignCurrentOrientation();
        Assert.Equal(original.OrientationVersion, reverted.OrientationVersion);
        Assert.NotEqual(original.AssignmentId, reverted.AssignmentId);
        Assert.Equal(OrientationStates.Assigned, store.GetOrientationStatus(identity.EmployeeId).State);
        Assert.Equal(2, RawCount(root.Path, "SELECT COUNT(*) FROM orientation_assignments WHERE orientation_version = $version", original.OrientationVersion));
        Assert.Equal(1, RawCount(root.Path, "SELECT COUNT(*) FROM orientation_assignments WHERE id = $id AND state = 'Stale'", original.AssignmentId));
    }

    [Fact]
    public void TerminalAttemptsAndSessionRotationCreateFreshAssignmentsWhileSameSessionRestartIsIdempotent()
    {
        using var root = new TempOrientationStore();
        string employeeId;
        string version;
        string rejectedAssignment;

        using (var store = root.Open())
        {
            var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            employeeId = identity.EmployeeId;
            store.RecordSession("ses-recovery", null);
            var first = store.ComposeAndAssignCurrentOrientation();
            version = first.OrientationVersion;
            var delivered = store.MarkOrientationDelivered(first.AssignmentId, version, "ses-recovery", first.AssignmentRevision);
            var bad = ValidEvidence(delivered.AssignmentId, employeeId, "ses-wrong", version, delivered.Revision);
            rejectedAssignment = store.ValidateAndRecordComprehension(bad).AssignmentId;

            var redelivery = store.ComposeAndAssignCurrentOrientation();
            Assert.NotEqual(rejectedAssignment, redelivery.AssignmentId);
            Assert.Equal(version, redelivery.OrientationVersion);
            var redelivered = store.MarkOrientationDelivered(
                redelivery.AssignmentId, version, "ses-recovery", redelivery.AssignmentRevision);
            Assert.Equal(OrientationStates.Delivered, redelivered.State);
            Assert.Equal(OrientationStates.Comprehended, store.ValidateAndRecordComprehension(
                ValidEvidence(redelivered.AssignmentId, employeeId, "ses-recovery", version, redelivered.Revision)).State);
            Assert.Equal(1, RawCount(root.Path, "SELECT COUNT(*) FROM orientation_assignments WHERE id = $id AND state = 'Stale'", rejectedAssignment));
        }

        using (var restarted = root.Open())
        {
            restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            var current = restarted.GetOrientationStatus(employeeId);
            var idempotent = restarted.ComposeAndAssignCurrentOrientation();
            Assert.Equal(current.AssignmentId, idempotent.AssignmentId);
            Assert.Equal(current.Revision, idempotent.AssignmentRevision);

            restarted.RecordSession("ses-rotated", null);
            var rotated = restarted.ComposeAndAssignCurrentOrientation();
            Assert.NotEqual(current.AssignmentId, rotated.AssignmentId);
            Assert.Equal(version, rotated.OrientationVersion);
            Assert.Equal("ses-rotated", rotated.SessionId);
            Assert.Equal(OrientationStates.Delivered, restarted.MarkOrientationDelivered(
                rotated.AssignmentId, version, "ses-rotated", rotated.AssignmentRevision).State);
            Assert.Equal(1, RawCount(root.Path, "SELECT COUNT(*) FROM orientation_assignments WHERE id = $id AND state = 'Stale'", current.AssignmentId));
        }
    }

    [Fact]
    public void EvidenceIsBoundToImmutableAssignmentAcrossAtoBtoAReplacement()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-assignment-binding", null);

        var first = store.ComposeAndAssignCurrentOrientation();
        var firstDelivered = store.MarkOrientationDelivered(
            first.AssignmentId, first.OrientationVersion, "ses-assignment-binding", first.AssignmentRevision);
        var staleEvidence = ValidEvidence(
            firstDelivered.AssignmentId,
            identity.EmployeeId,
            "ses-assignment-binding",
            firstDelivered.OrientationVersion,
            firstDelivered.Revision);

        var overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(overview.Id, "Temporary B instructions.", overview.Revision);
        _ = store.ComposeAndAssignCurrentOrientation();
        overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(overview.Id, OrganizationSeed.OrganizationInstructions, overview.Revision);
        var replacement = store.ComposeAndAssignCurrentOrientation();
        var replacementDelivered = store.MarkOrientationDelivered(
            replacement.AssignmentId,
            replacement.OrientationVersion,
            "ses-assignment-binding",
            replacement.AssignmentRevision);

        Assert.Equal(first.OrientationVersion, replacementDelivered.OrientationVersion);
        Assert.NotEqual(first.AssignmentId, replacementDelivered.AssignmentId);
        Assert.Equal(firstDelivered.Revision, replacementDelivered.Revision);
        Assert.Throws<OrganizationConcurrencyException>(() => store.ValidateAndRecordComprehension(staleEvidence));
        Assert.Equal(OrientationStates.Delivered, store.GetOrientationStatus(identity.EmployeeId).State);
    }

    [Fact]
    public void ReloadConfirmationIsRequiredBeforeComprehensionAndPreservesAssignmentHistory()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-reload", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(
            assignment.AssignmentId,
            assignment.OrientationVersion,
            "ses-reload",
            assignment.AssignmentRevision,
            requiredRuntimeGeneration: 10);

        Assert.True(delivered.RestartRequired);
        Assert.Contains(DispatchHoldReasons.OrientationReloadRequired, delivered.HoldReasons);
        Assert.False(delivered.Ready);
        Assert.Throws<OrganizationConcurrencyException>(() => store.ValidateAndRecordComprehension(
            ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-reload", delivered.OrientationVersion, delivered.Revision)));

        var loaded = store.ConfirmOrientationLoaded(
            delivered.AssignmentId,
            delivered.OrientationVersion,
            "ses-reload",
            runtimeGeneration: 10);
        Assert.False(loaded.RestartRequired);
        Assert.DoesNotContain(DispatchHoldReasons.OrientationReloadRequired, loaded.HoldReasons);
        Assert.Throws<OrganizationConcurrencyException>(() => store.ConfirmOrientationLoaded(
            delivered.AssignmentId,
            delivered.OrientationVersion,
            "ses-reload",
            runtimeGeneration: 9));
        var comprehended = store.ValidateAndRecordComprehension(
            ValidEvidence(loaded.AssignmentId, identity.EmployeeId, "ses-reload", loaded.OrientationVersion, loaded.Revision));
        Assert.Equal(OrientationStates.Comprehended, comprehended.State);
        Assert.Equal(delivered.AssignmentId, comprehended.AssignmentId);
    }

    [Fact]
    public void RoleInstructionUpdateRevisesFragmentAndStalesOrientationWithoutChangingIdentity()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-role-update", "Preserved");
        var original = store.ComposeAndAssignCurrentOrientation();
        _ = store.MarkOrientationDelivered(
            original.AssignmentId, original.OrientationVersion, "ses-role-update", original.AssignmentRevision);
        var before = store.GetOverview();
        var role = Assert.Single(before.Roles);

        var updated = store.UpdateRoleInstructions(
            role.Id,
            "Operate the control host.\nExplicitly ask the owner before changing standing policy.",
            role.Revision);
        var updatedRole = Assert.Single(updated.Roles);
        Assert.Equal(role.Id, updatedRole.Id);
        Assert.Equal(role.Revision + 1, updatedRole.Revision);
        Assert.Equal(
            "Operate the control host.\nExplicitly ask the owner before changing standing policy.",
            updatedRole.StandingInstructions);
        Assert.Throws<OrganizationConcurrencyException>(() => store.UpdateRoleInstructions(
            role.Id,
            "stale update",
            role.Revision));

        var stale = store.GetOrientationStatus(identity.EmployeeId);
        Assert.Equal(OrientationStates.Stale, stale.State);
        Assert.Contains(DispatchHoldReasons.PolicyUpdate, stale.HoldReasons);
        var replacement = store.ComposeAndAssignCurrentOrientation();
        Assert.NotEqual(original.AssignmentId, replacement.AssignmentId);
        Assert.NotEqual(original.OrientationVersion, replacement.OrientationVersion);
        Assert.Equal(identity.EmployeeId, replacement.EmployeeId);
        Assert.Equal(identity.RuntimeBindingId, replacement.RuntimeBindingId);
        Assert.Equal("ses-role-update", replacement.SessionId);
        Assert.Contains("Explicitly ask the owner", replacement.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersededAttemptFailureIsHistoricalIdempotentAndDoesNotMutateReplacement()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-historical-failure", null);
        var originalArtifact = store.ComposeAndAssignCurrentOrientation();
        var original = store.MarkOrientationDelivered(
            originalArtifact.AssignmentId,
            originalArtifact.OrientationVersion,
            "ses-historical-failure",
            originalArtifact.AssignmentRevision);

        var overview = store.GetOverview();
        store.UpdateOrganizationBasicInstructions(
            overview.Id,
            "Replacement instructions while the original attempt is live.",
            overview.Revision);
        var replacementArtifact = store.ComposeAndAssignCurrentOrientation();
        var replacement = store.MarkOrientationDelivered(
            replacementArtifact.AssignmentId,
            replacementArtifact.OrientationVersion,
            "ses-historical-failure",
            replacementArtifact.AssignmentRevision,
            requiredRuntimeGeneration: 7);

        var first = store.RecordComprehensionFailure(
            original.AssignmentId,
            identity.EmployeeId,
            "ses-historical-failure",
            original.OrientationVersion,
            original.Revision,
            "Host-authored historical failure.");
        var duplicate = store.RecordComprehensionFailure(
            original.AssignmentId,
            identity.EmployeeId,
            "ses-historical-failure",
            original.OrientationVersion,
            original.Revision,
            "Host-authored historical failure.");

        Assert.Equal(replacement.AssignmentId, first.AssignmentId);
        Assert.Equal(replacement.State, first.State);
        Assert.Equal(replacement.Revision, first.Revision);
        Assert.Equal(replacement.HoldReasons, first.HoldReasons);
        Assert.Equal(first.AssignmentId, duplicate.AssignmentId);
        Assert.Equal(first.State, duplicate.State);
        Assert.Equal(first.Revision, duplicate.Revision);
        Assert.Equal(first.HoldReasons, duplicate.HoldReasons);
        Assert.Equal(OrientationStates.Stale, RawString(
            root.Path,
            $"SELECT state FROM orientation_assignments WHERE id = '{original.AssignmentId}'"));
        Assert.Equal(OrientationEvidenceSources.LiveModel, RawString(
            root.Path,
            $"SELECT evidence_source FROM orientation_assignments WHERE id = '{original.AssignmentId}'"));
        Assert.Equal(1, RawCount(
            root.Path,
            "SELECT COUNT(*) FROM orientation_evidence WHERE assignment_id = $id AND outcome = 'Failed'",
            original.AssignmentId));
        Assert.Throws<OrganizationConcurrencyException>(() => store.RecordComprehensionFailure(
            original.AssignmentId,
            identity.EmployeeId,
            "ses-wrong",
            original.OrientationVersion,
            original.Revision,
            "Host-authored historical failure."));
    }

    [Fact]
    public void TimedOutAttemptCanBeRedeliveredAfterStoreRestart()
    {
        using var root = new TempOrientationStore();
        string employeeId;
        string version;
        string timedOutAssignment;
        using (var store = root.Open())
        {
            var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            employeeId = identity.EmployeeId;
            store.RecordSession("ses-timeout-restart", null);
            var assignment = store.ComposeAndAssignCurrentOrientation();
            version = assignment.OrientationVersion;
            var delivered = store.MarkOrientationDelivered(
                assignment.AssignmentId, version, "ses-timeout-restart", assignment.AssignmentRevision);
            var timedOut = store.RecordComprehensionTimeout(
                delivered.AssignmentId,
                employeeId,
                "ses-timeout-restart",
                version,
                delivered.Revision);
            timedOutAssignment = timedOut.AssignmentId;
            Assert.Equal(OrientationStates.TimedOut, timedOut.State);
        }

        using var restarted = root.Open();
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var retry = restarted.ComposeAndAssignCurrentOrientation();
        Assert.NotEqual(timedOutAssignment, retry.AssignmentId);
        Assert.Equal(version, retry.OrientationVersion);
        Assert.Equal(OrientationStates.Delivered, restarted.MarkOrientationDelivered(
            retry.AssignmentId, version, "ses-timeout-restart", retry.AssignmentRevision).State);
        Assert.Equal(1, RawCount(root.Path, "SELECT COUNT(*) FROM orientation_assignments WHERE id = $id AND state = 'Stale'", timedOutAssignment));
    }

    [Fact]
    public void MismatchIsRejectedAndCannotBeReplayed()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-evidence", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-evidence", assignment.AssignmentRevision);
        var bad = ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-wrong", delivered.OrientationVersion, delivered.Revision);

        var rejected = store.ValidateAndRecordComprehension(bad);
        Assert.Equal(OrientationStates.Rejected, rejected.State);
        Assert.Contains(DispatchHoldReasons.OrientationFailed, rejected.HoldReasons);
        Assert.Throws<OrganizationConcurrencyException>(() => store.ValidateAndRecordComprehension(bad));
    }

    [Fact]
    public void NullEvidenceFieldsAreValidationErrorsBeforeCanonicalization()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-null-evidence", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(
            assignment.AssignmentId, assignment.OrientationVersion, "ses-null-evidence", assignment.AssignmentRevision);
        var valid = ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-null-evidence", delivered.OrientationVersion, delivered.Revision);

        Assert.Throws<OrganizationValidationException>(() => store.ValidateAndRecordComprehension(valid with { Identity = null! }));
        Assert.Throws<OrganizationValidationException>(() => store.ValidateAndRecordComprehension(valid with { Duties = null }));
        Assert.Equal(OrientationStates.Delivered, store.GetOrientationStatus(identity.EmployeeId).State);
    }

    [Fact]
    public void NonWaivableHostDenyCannotBeGrantedAndPermissionDefaultsToReject()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-permission", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-permission", assignment.AssignmentRevision);
        store.ValidateAndRecordComprehension(ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-permission", delivered.OrientationVersion, delivered.Revision));

        Assert.Throws<OrganizationValidationException>(() => store.CreatePermissionGrant(new PermissionGrantRequest(
            identity.EmployeeId, "rst-host-secrets", "read", "secret:key", DateTimeOffset.UtcNow.AddHours(1), 1, "deny-cannot-waive")));

        var denied = store.EvaluatePermission(identity.EmployeeId, "ses-permission", 1, "read", "secret:key");
        Assert.False(denied.Allowed);
        Assert.Equal("rst-host-secrets", denied.RestrictionId);
        var unknown = store.EvaluatePermission(identity.EmployeeId, "ses-permission", 1, "mystery", "unknown:claim");
        Assert.False(unknown.Allowed);
    }

    [Fact]
    public void OverlappingWaivableAndNonWaivableMatchesRejectIndependentOfOrder()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-layers", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-layers", assignment.AssignmentRevision);
        store.ValidateAndRecordComprehension(ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-layers", delivered.OrientationVersion, delivered.Revision));

        // A waivable overlapping restriction is inserted both before and after the
        // non-waivable key so the outcome cannot depend on evaluation order.
        foreach (var key in new[] { "aaa-overlap-waivable", "zzz-overlap-waivable" })
        {
            InsertOverlappingWaivable(root.Path, key);
            var decision = store.EvaluatePermission(identity.EmployeeId, "ses-layers", 1, "read", "secret:key");
            Assert.False(decision.Allowed);
            Assert.Equal("rst-host-secrets", decision.RestrictionId);
        }

        var matchedJson = RawString(root.Path, "SELECT matched_restriction_ids FROM permission_audit ORDER BY rowid DESC LIMIT 1");
        var matched = System.Text.Json.JsonSerializer.Deserialize<string[]>(matchedJson);
        Assert.NotNull(matched);
        Assert.Equal(3, matched!.Length);
        Assert.Contains("rst-host-secrets", matched);
        Assert.Contains("rst-aaa-overlap-waivable", matched);
        Assert.Contains("rst-zzz-overlap-waivable", matched);
        Assert.Equal(matched.Order(StringComparer.Ordinal), matched);
    }

    [Fact]
    public void LowerLayerDenyRejectsAndCannotBeWaivedByAnUnrelatedGrant()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-lower", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-lower", assignment.AssignmentRevision);
        store.ValidateAndRecordComprehension(ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-lower", delivered.OrientationVersion, delivered.Revision));

        // A role-layer deny (no code work) is not a host restriction, and no owner
        // grant exists for it: the claim must still be rejected.
        var denied = store.EvaluatePermission(identity.EmployeeId, "ses-lower", 1, "edit", "code-work:repository");
        Assert.False(denied.Allowed);
        Assert.Equal("rst-role-no-code-work", denied.RestrictionId);

        Assert.Throws<OrganizationValidationException>(() => store.CreatePermissionGrant(new PermissionGrantRequest(
            identity.EmployeeId, "rst-role-no-code-work", "edit", "code-work:repository", DateTimeOffset.UtcNow.AddHours(1), 1, "lower-layer-waiver")));
    }

    [Fact]
    public void PermissionSessionMismatchPersistsRejectedRequestAndAuditWithoutForeignKeyFailure()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-current", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-current", assignment.AssignmentRevision);
        store.ValidateAndRecordComprehension(ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-current", delivered.OrientationVersion, delivered.Revision));

        var absent = store.EvaluatePermission(identity.EmployeeId, null, 7, "read", "secret:key");
        var wrong = store.EvaluatePermission(identity.EmployeeId, "ses-wrong", 8, "read", "secret:key");

        Assert.False(absent.Allowed);
        Assert.False(wrong.Allowed);
        Assert.Equal(2, RawCount(root.Path, "SELECT COUNT(*) FROM permission_requests WHERE session_id IS NULL AND status = 'rejected'", string.Empty));
        Assert.Equal(2, RawCount(root.Path, "SELECT COUNT(*) FROM permission_audit WHERE decision = 'rejected'", string.Empty));
    }

    [Fact]
    public void WaivableGrantIsScopedExpiresAndRevokesUnderRevision()
    {
        using var root = new TempOrientationStore();
        using var store = root.Open();
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses-grant", null);
        var assignment = store.ComposeAndAssignCurrentOrientation();
        var delivered = store.MarkOrientationDelivered(assignment.AssignmentId, assignment.OrientationVersion, "ses-grant", assignment.AssignmentRevision);
        store.ValidateAndRecordComprehension(ValidEvidence(delivered.AssignmentId, identity.EmployeeId, "ses-grant", delivered.OrientationVersion, delivered.Revision));
        var request = new PermissionGrantRequest(identity.EmployeeId, "rst-host-safe-diagnostic-example", "read", "diagnostic:public", DateTimeOffset.UtcNow.AddHours(1), 1, "grant-1");
        var grant = store.CreatePermissionGrant(request);
        Assert.Equal(grant.Id, store.CreatePermissionGrant(request).Id);
        // Phase 1 grants are staged owner records only. The pinned ACP request
        // shape has no host-derived canonical resource, so execution remains rejected.
        Assert.False(store.EvaluatePermission(identity.EmployeeId, "ses-grant", 2, "read", "diagnostic:public").Allowed);
        Assert.False(store.EvaluatePermission(identity.EmployeeId, "ses-grant", 2, "read", "diagnostic:other").Allowed);
        var revoked = store.RevokePermissionGrant(grant.Id, grant.Revision);
        Assert.NotNull(revoked.RevokedAt);
        Assert.False(store.EvaluatePermission(identity.EmployeeId, "ses-grant", 2, "read", "diagnostic:public").Allowed);
        Assert.Throws<OrganizationConcurrencyException>(() => store.RevokePermissionGrant(grant.Id, grant.Revision));
    }

    [Fact]
    public void LinuxNativeStatGuardRejectsUnsupportedArchitectures()
    {
        OrientationArtifactPublisher.EnsureSupportedLinuxArchitecture(System.Runtime.InteropServices.Architecture.X64);
        Assert.Throws<PlatformNotSupportedException>(() =>
            OrientationArtifactPublisher.EnsureSupportedLinuxArchitecture(System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Fact]
    public void ArtifactPublicationIsExactAtomicAndRejectsSymlinkDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempOrientationStore();
        var real = Path.Combine(root.Directory, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root.Directory, "link");
        Directory.CreateSymbolicLink(link, real);
        var content = "exact utf-8 orientation\n";
        var artifact = new OrientationArtifact("ora", "emp", "rtb", null, OrientationComposer.Hash(content), content, "orientation-current.md", 1);
        Assert.Throws<IOException>(() => OrientationArtifactPublisher.Publish(link, artifact));
        OrientationArtifactPublisher.Publish(real, artifact);
        Assert.Equal(content, File.ReadAllText(Path.Combine(real, artifact.ArtifactFileName)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(Path.Combine(real, artifact.ArtifactFileName)));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string created);

    private static void InsertOverlappingWaivable(string path, string key)
    {
        ExecuteRaw(
            path,
            $"""
            INSERT INTO permission_restrictions (
                id, policy_id, fragment_id, layer, stable_key,
                tool_pattern, resource_pattern, description, waivable, revision)
            SELECT 'rst-{key}', id, NULL, 'host', '{key}',
                   '*', 'secret:*', 'Overlapping waivable test restriction.', 1, 1
            FROM permission_policies WHERE active = 1;
            """);
    }

    private static void ExecuteRaw(string path, string sql)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string RawString(string path, string sql)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static int RawCount(string path, string sql, string value)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$version", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$version", value);
        }
        else if (sql.Contains("$id", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$id", value);
        }
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void PublicationRejectsHardLinkedAndIrregularDestinations()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempOrientationStore();
        var directory = Path.Combine(root.Directory, "publish");
        Directory.CreateDirectory(directory);
        var content = "exact utf-8 orientation\n";
        var artifact = new OrientationArtifact("ora", "emp", "rtb", null, OrientationComposer.Version(content), content, "orientation-current.md", 1);
        var destination = Path.Combine(directory, artifact.ArtifactFileName);

        OrientationArtifactPublisher.Publish(directory, artifact);
        Assert.Equal(content, File.ReadAllText(destination));

        // A second hard link to the destination means another identity retains a
        // writable alias of the same inode; publication must refuse it.
        var alias = Path.Combine(directory, "alias.md");
        Assert.Equal(0, Link(destination, alias));
        Assert.Throws<IOException>(() => OrientationArtifactPublisher.Publish(directory, artifact));
        File.Delete(alias);

        // After the alias is removed the publication succeeds again and the
        // verified inode carries exactly the assigned content.
        OrientationArtifactPublisher.Publish(directory, artifact);
        Assert.Equal(content, File.ReadAllText(destination));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(destination));
    }

    [Fact]
    public void ConcurrentPublicationsSerializeAndConvergeOnTheAssignedVersion()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempOrientationStore();
        var directory = Path.Combine(root.Directory, "parallel");
        Directory.CreateDirectory(directory);
        var content = "concurrent orientation payload\n";
        var artifact = new OrientationArtifact("ora", "emp", "rtb", null, OrientationComposer.Version(content), content, "orientation-current.md", 1);

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 16, _ =>
        {
            try
            {
                OrientationArtifactPublisher.Publish(directory, artifact);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        });

        Assert.Empty(failures);
        Assert.Equal(content, File.ReadAllText(Path.Combine(directory, artifact.ArtifactFileName)));
        Assert.Single(Directory.GetFiles(directory));
    }

    private static OrientationEvidenceRequest ValidEvidence(string assignment, string employee, string session, string version, int revision) => new(
        assignment, employee, session, version, OrganizationSeed.AdoptedEmployeeDisplayName, OrganizationSeed.OperationsDisplayName, "owner",
        ["operate and maintain the control host", "inspect runtime health and sanitized diagnostics", "explain organization state", "request owner-authorized changes"],
        ["no secrets or controller-private state", "no unrestricted Docker, GitHub, or host authority", "no autonomous hiring, provisioning, delegation, or dispatch", "no Fleet or V1", "no cross-employee history"],
        "escalate uncertainty, failed controls, suspected secret exposure, and irreversible effects before retrying", revision);

    private sealed class TempOrientationStore : IDisposable
    {
        public TempOrientationStore() { Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orientation-tests-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory); }
        public string Directory { get; }
        public string Path => System.IO.Path.Combine(Directory, OrganizationStore.DatabaseFileName);
        public OrganizationStore Open() => new(Path, lockTimeout: TimeSpan.FromSeconds(2));
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { System.IO.Directory.Delete(Directory, true); } catch { } }
    }
}
