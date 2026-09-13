using HVO.AgentControl.Core;
using HVO.AgentControl.Services;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class DevContainerCliOperationAdapterTests
{
    private static DevContainerCliOperationRequest Request() => new("request-43", new() { Id = "host-43", Name = "Host" },
        new() { Id = "project-43", Name = "Project", RepositoryUrl = "https://example.test/project" },
        new("runtime-43", "Runtime", 1, 2, "host-43", RuntimeEnvironmentKind.ManagedDevcontainer, "project-43",
            ".devcontainer/devcontainer.json", "Configured", 1, 1, false, 1, 2));

    [Fact]
    public void ExecuteReportsMissingAuthorityWithoutPretendingToProvision()
    {
        IDevContainerCliOperationAdapter adapter = new DevContainerCliOperationAdapter();
        var result = adapter.Execute(Request());
        Assert.Equal("Unsupported", result.Status); Assert.Equal("missing_provisioner_authority", result.Receipt);
        Assert.NotNull(result.Requested); Assert.Equal("0.89.0", result.Resolved!.CliVersion);
        Assert.NotSame(result.Requested, result.Resolved); Assert.Empty(result.Requested.Labels);
        Assert.Equal(result.Resolved.IntentDigest, result.IntentDigest); Assert.Equal(result.IntentDigest, result.Resolved.Labels["hvo.agentcontrol.intent-digest"]);
        Assert.Null(result.Observed); Assert.Contains("not invoked", result.Output);
    }

    [Fact]
    public void ReconciliationUsesImmutableRequestLabelsAndDoesNotRetryMissingObservation()
    {
        var adapter = new DevContainerCliOperationAdapter(); var request = Request();
        var missing = adapter.Reconcile(request, []);
        Assert.Equal("Uncertain", missing.Status); Assert.Equal("labels_not_observed", missing.Receipt);
        var labels = new Dictionary<string, string>(missing.Resolved!.Labels) { ["hvo.agentcontrol.request-id"] = "other" };
        var mismatch = adapter.Reconcile(request, [new("container-43", labels)]);
        Assert.Equal("Uncertain", mismatch.Status); Assert.Equal("labels_not_observed", mismatch.Receipt);
        var observed = adapter.Reconcile(request, [new("container-43", missing.Resolved.Labels, new('x', 5000))]);
        Assert.Equal("Observed", observed.Status); Assert.Equal("labels_observed", observed.Receipt);
        Assert.Equal(4096, observed.Output.Length);
    }

    [Fact]
    public void ReconciliationRejectsConflictingIntentAndMultipleExactOwnershipMatches()
    {
        var adapter = new DevContainerCliOperationAdapter(); var request = Request();
        var first = adapter.Execute(request);
        var changedPath = adapter.Reconcile(request with { Environment = request.Environment with { DevcontainerPath = ".devcontainer/other.json" } }, []);
        var changedVersion = adapter.Reconcile(request with { CliVersion = "0.90.0" }, []);
        Assert.Equal("request_intent_conflict", changedPath.Receipt); Assert.Equal("request_intent_conflict", changedVersion.Receipt);

        var oneExactAndMismatch = adapter.Reconcile(request, [new("exact", first.Resolved!.Labels), new("other", new Dictionary<string, string>())]);
        Assert.Equal("Observed", oneExactAndMismatch.Status);
        var multiple = adapter.Reconcile(request, [new("first", first.Resolved.Labels), new("second", first.Resolved.Labels)]);
        Assert.Equal("Uncertain", multiple.Status); Assert.Equal("multiple_labels_observed", multiple.Receipt);
        var blankAndValid = adapter.Reconcile(request, [new("", first.Resolved.Labels), new("valid", first.Resolved.Labels)]);
        Assert.Equal("Uncertain", blankAndValid.Status); Assert.Equal("multiple_labels_observed", blankAndValid.Receipt);
        var soleBlank = adapter.Reconcile(request, [new(null, first.Resolved.Labels)]);
        Assert.Equal("Uncertain", soleBlank.Status); Assert.Equal("container_identity_missing", soleBlank.Receipt);
    }

    [Fact]
    public void RegisteredIdentityAndConfigurationMismatchesFailBeforeAnyOperation()
    {
        var result = new DevContainerCliOperationAdapter().Execute(Request() with
        {
            Environment = Request().Environment with { ConfigurationProjectId = "other-project" }
        });
        Assert.Equal("Failed", result.Status); Assert.Equal("configuration_project_identity_mismatch", result.Receipt);
        Assert.NotNull(result.Requested); Assert.Null(result.Resolved); Assert.Null(result.Observed);
    }
}
