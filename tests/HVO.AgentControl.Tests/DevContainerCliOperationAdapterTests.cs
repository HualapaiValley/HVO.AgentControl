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
        Assert.Null(result.Observed); Assert.Contains("not invoked", result.Output);
    }

    [Fact]
    public void ReconciliationUsesImmutableRequestLabelsAndDoesNotRetryMissingObservation()
    {
        var adapter = new DevContainerCliOperationAdapter(); var request = Request();
        var missing = adapter.Reconcile(request, null);
        Assert.Equal("Uncertain", missing.Status); Assert.Equal("observation_missing", missing.Receipt);
        var labels = new Dictionary<string, string>(missing.Resolved!.Labels) { ["hvo.agentcontrol.request-id"] = "other" };
        var mismatch = adapter.Reconcile(request, new("container-43", labels));
        Assert.Equal("Uncertain", mismatch.Status); Assert.Equal("labels_not_observed", mismatch.Receipt);
        var observed = adapter.Reconcile(request, new("container-43", missing.Resolved.Labels, new('x', 5000)));
        Assert.Equal("Observed", observed.Status); Assert.Equal("labels_observed", observed.Receipt);
        Assert.Equal(4096, observed.Output.Length);
    }

    [Fact]
    public void RegisteredIdentityAndConfigurationMismatchesFailBeforeAnyOperation()
    {
        var result = new DevContainerCliOperationAdapter().Execute(Request() with
        {
            Environment = Request().Environment with { ConfigurationProjectId = "other-project" }
        });
        Assert.Equal("Failed", result.Status); Assert.Equal("configuration_project_identity_mismatch", result.Receipt);
        Assert.Null(result.Resolved); Assert.Null(result.Observed);
    }
}
