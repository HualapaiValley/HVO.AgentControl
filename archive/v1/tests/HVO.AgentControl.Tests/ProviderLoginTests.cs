using System.Net;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderLoginTests
{
    [Fact]
    public void DiscoversHeadlessMethodWithoutAssumingIndex()
    {
        using var methods = JsonDocument.Parse("""{"openai":[{"type":"api","label":"API key"},{"type":"oauth","label":"ChatGPT Pro/Plus (browser)"},{"type":"oauth","label":"ChatGPT Pro/Plus (headless)"}]}""");
        Assert.Equal(2, ProviderLoginService.HeadlessMethod(methods.RootElement));
        using var missing = JsonDocument.Parse("""{"openai":[{"type":"oauth","label":"ChatGPT Pro/Plus (browser)"}]}""");
        Assert.Throws<ControlException>(() => ProviderLoginService.HeadlessMethod(missing.RootElement));
    }

    [Theory]
    [InlineData("https://auth.openai.com.evil.example/codex/device", "auto", "Enter code: ABCD-E1234")]
    [InlineData("javascript:alert(1)", "auto", "Enter code: ABCD-E1234")]
    [InlineData("https://auth.openai.com/codex/device", "code", "Enter code: ABCD-E1234")]
    [InlineData("https://auth.openai.com/codex/device", "auto", "Enter code: ABCD-E1234\nVisit another site")]
    public void RejectsUntrustedApprovalInstructions(string url, string method, string instructions)
    {
        var value = JsonSerializer.SerializeToElement(new { url, method, instructions });
        Assert.Throws<ControlException>(() => ProviderLoginService.BrowserInstructions(value));
    }

    [Fact]
    public async Task UsesScopedNativeOAuthRoutesWithoutHandlingTokens()
    {
        var handler = new AuthHandler();
        using var api = new OpenCodeClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        var methods = await api.ProviderAuthMethods("/workspace/a b", CancellationToken.None);
        var method = ProviderLoginService.HeadlessMethod(methods);
        var authorization = await api.AuthorizeChatGpt("/workspace/a b", method, CancellationToken.None);
        Assert.Equal(("https://auth.openai.com/codex/device", "ABCD-E1234"), ProviderLoginService.BrowserInstructions(authorization));
        Assert.True((await api.CompleteChatGpt("/workspace/a b", method, CancellationToken.None)).GetBoolean());
        Assert.Equal(3, handler.Calls);
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("?directory=%2Fworkspace%2Fa%20b", request.RequestUri!.Query);
            var path = request.RequestUri.AbsolutePath;
            if (path != "/provider/auth")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("{\"method\":1}", await request.Content!.ReadAsStringAsync(cancellationToken));
            }
            var json = path switch
            {
                "/provider/auth" => """{"openai":[{"type":"api","label":"API key"},{"type":"oauth","label":"ChatGPT Pro/Plus (headless)"}]}""",
                "/provider/openai/oauth/authorize" => """{"url":"https://auth.openai.com/codex/device","method":"auto","instructions":"Enter code: ABCD-E1234"}""",
                "/provider/openai/oauth/callback" => "true",
                _ => throw new InvalidOperationException("Unexpected native route")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
