using System.Text.Json;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpPermissionTests
{
    [Fact]
    public void SelectsRejectOnceOption()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}""");

        Assert.Equal("reject_once", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void PinnedRealShapeSelectsGenericReject()
    {
        Assert.Equal("reject", PermissionPolicy.SelectRejectPermissionOption(["once", "always", "reject"]));
    }

    [Fact]
    public void RejectOptionSelectorPrefersRejectOnceThenRejectThenRejectAlways()
    {
        Assert.Equal("reject_once", PermissionPolicy.SelectRejectPermissionOption(["reject_always", "reject", "reject_once"]));
        Assert.Equal("reject", PermissionPolicy.SelectRejectPermissionOption(["reject_always", "reject"]));
        Assert.Equal("reject_always", PermissionPolicy.SelectRejectPermissionOption(["reject_always"]));
    }

    [Fact]
    public void RejectOptionSelectorNeverSelectsAllowLikeOrUnknownNames()
    {
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["once"]));
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["always"]));
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["allow_once", "allow_always"]));
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["reject_once_allow", "not_reject", "Reject"]));
    }

    [Fact]
    public void RejectOptionSelectorWithoutAnyRejectThrowsCompatibilityFailure()
    {
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption([]));
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["once", "always"]));
    }

    [Fact]
    public void VettedSafeRejectListIsOrdinalAndToleratesDuplicates()
    {
        // The plain-list path operates on the durable, already-vetted safe list;
        // duplicate entries there are harmless. Frame parsing below is strict.
        Assert.Equal("reject", PermissionPolicy.SelectRejectPermissionOption(["reject", "reject"]));
        Assert.Equal("reject_once", PermissionPolicy.SelectRejectPermissionOption(["reject_once", "reject_once", "reject"]));
        Assert.Throws<WorkerPermissionOptionsUnsupportedException>(() => PermissionPolicy.SelectRejectPermissionOption(["REJECT_ONCE"]));
    }

    [Theory]
    // Both orderings of a duplicate reject ID with a contradictory allow kind:
    // the repeated ID is vetoed, so no reject is selected and the request cancels.
    [InlineData("""{"options":[{"optionId":"reject","kind":"allow_once"},{"optionId":"reject","kind":"reject_once"}]}""")]
    [InlineData("""{"options":[{"optionId":"reject","kind":"reject_once"},{"optionId":"reject","kind":"allow_once"}]}""")]
    // Identical individually eligible duplicates are also vetoed entirely.
    [InlineData("""{"options":[{"optionId":"reject_once","kind":"reject_once"},{"optionId":"reject_once","kind":"reject_once"}]}""")]
    [InlineData("""{"options":[{"optionId":"reject","kind":"reject_once"},{"optionId":"reject","kind":"reject_once"}]}""")]
    public void DuplicateOptionIdVetoesThatIdEntirely(string json)
    {
        Assert.Null(PermissionPolicy.SelectRejectOption(Parse(json)));
        var result = Assert.IsType<Dictionary<string, object?>>(PermissionPolicy.BuildRejection(Parse(json)));
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("cancelled", outcome["outcome"]);
    }

    [Fact]
    public void DuplicateIneligibleIdDoesNotVetoOtherEligibleId()
    {
        // Only the repeated ID is vetoed; an unrelated eligible ID is unaffected.
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}""");

        Assert.Equal("reject_once", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void DuplicateRejectOnceFallsThroughToSingletonReject()
    {
        // Vetoing the duplicated one-shot ID must not promote a persistent
        // reject_always; the singleton generic `reject` is still eligible.
        var parameters = Parse(
            """{"options":[{"optionId":"reject_once","kind":"reject_once"},{"optionId":"reject_once","kind":"reject_once"},{"optionId":"reject","kind":"reject_once"},{"optionId":"reject_always","kind":"reject_always"}]}""");

        Assert.Equal("reject", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void SelectsExactRejectAlwaysOptionId()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_always","kind":"reject_always"}]}""");

        Assert.Equal("reject_always", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void RejectKindCannotAuthorizeVendorIdOrContradictoryKnownId()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"vendor-once","kind":"reject_once"},{"optionId":"reject","kind":"allow_once"}]}""");

        Assert.Null(PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void PinnedGenericShapeReturnsAssociatedRejectOptionId()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"once","kind":"allow_once"},{"optionId":"always","kind":"allow_always"},{"optionId":"reject","kind":"reject_once"}]}""");

        Assert.Equal("reject", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void ExactRejectIdWithAllowUnknownOrMalformedKindIsIneligible()
    {
        Assert.Null(PermissionPolicy.SelectRejectOption(Parse(
            """{"options":[{"optionId":"reject_once","kind":"allow_once"},{"optionId":"reject","kind":"vendor_reject"},{"optionId":"reject_always","kind":7}]}""")));
    }

    [Fact]
    public void MissingKindPermitsOnlyExactKnownRejectId()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"vendor-no-kind"},{"optionId":"reject"}]}""");

        Assert.Equal("reject", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void ExactFixedIdWinsWhileVendorRejectKindsRemainIneligible()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"reject","kind":"reject_always"},{"optionId":"vendor-always","kind":"reject_always"},{"optionId":"vendor-once","kind":"reject_once"}]}""");

        Assert.Equal("reject", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void RejectKindsCannotAuthorizeVendorOrAllowIds()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"vendor-always","kind":"reject_always"},{"optionId":"vendor-once","kind":"reject_once"},{"optionId":"allow_once","kind":"reject_once"},{"optionId":"allow_always","kind":"reject_always"}]}""");

        Assert.Null(PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void KindFallbackNeverUsesNamesSubstringsOrAllowKinds()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"not_reject","name":"Reject this request","kind":"allow_once"},{"optionId":"vendor-always","kind":"allow_always"},{"optionId":"vendor-reject","kind":"reject-later"}]}""");

        Assert.Null(PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void NeverSelectsAnAllowOption()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"}]}""");

        Assert.Null(PermissionPolicy.SelectRejectOption(parameters));
        var result = Assert.IsType<Dictionary<string, object?>>(PermissionPolicy.BuildRejection(parameters));
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("cancelled", outcome["outcome"]);
    }

    [Fact]
    public void BuildRejectionSelectsRejectOutcome()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}""");

        var result = Assert.IsType<Dictionary<string, object?>>(PermissionPolicy.BuildRejection(parameters));
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("selected", outcome["outcome"]);
        Assert.Equal("reject_once", outcome["optionId"]);
    }

    [Fact]
    public void PinnedShapeTreatsTitleAsUntrustedAuditText()
    {
        var parameters = Parse(
            """{"toolCall":{"toolCallId":"tc-1","kind":"read","title":"diagnostic:public","status":"pending"},"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}""");

        Assert.True(PermissionPolicy.TryParseAuditClaim(parameters, out var tool, out var resource));
        Assert.Equal("read", tool);
        Assert.Equal(PermissionPolicy.UntrustedResource, resource);
        Assert.NotEqual("diagnostic:public", resource);
    }

    [Fact]
    public void SpoofedSafeTitleWithSecretIntentCannotBecomeAnAllowableResource()
    {
        var parameters = Parse(
            """{"toolCall":{"kind":"read","title":"diagnostic:public","rawInput":{"path":"/run/agentcontrol-secrets/key"}},"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"reject_once","kind":"reject_once"}]}""");

        Assert.True(PermissionPolicy.TryParseAuditClaim(parameters, out _, out var resource));
        Assert.Equal(PermissionPolicy.UntrustedResource, resource);
        var result = Assert.IsType<Dictionary<string, object?>>(PermissionPolicy.BuildRejection(parameters));
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("reject_once", outcome["optionId"]);
    }

    [Fact]
    public void OversizedOrUnknownPermissionClaimFailsParsing()
    {
        Assert.False(PermissionPolicy.TryParseAuditClaim(Parse("{}"), out _, out _));
        var oversized = Parse(JsonSerializer.Serialize(new
        {
            toolCall = new { kind = new string('x', 600), title = "ignored" },
            options = Array.Empty<object>(),
        }));
        Assert.False(PermissionPolicy.TryParseAuditClaim(oversized, out _, out _));
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
