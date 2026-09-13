using System.Text.Json;
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
    public void FallsBackToRejectAlways()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","kind":"allow_once"},{"optionId":"always_no","kind":"reject_always"}]}""");

        Assert.Equal("always_no", PermissionPolicy.SelectRejectOption(parameters));
    }

    [Fact]
    public void FallsBackToRejectName()
    {
        var parameters = Parse(
            """{"options":[{"optionId":"allow_once","name":"Allow"},{"optionId":"option-2","name":"Reject this"}]}""");

        Assert.Equal("option-2", PermissionPolicy.SelectRejectOption(parameters));
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
    public void MissingOptionsCancels()
    {
        var result = Assert.IsType<Dictionary<string, object?>>(PermissionPolicy.BuildRejection(Parse("{}")));
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("cancelled", outcome["outcome"]);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
