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
