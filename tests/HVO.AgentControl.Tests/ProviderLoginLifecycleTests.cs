using System.Net;
using System.Net.Http.Json;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderLoginLifecycleTests
{
    [Fact]
    public async Task OwnerAuthenticationAndCsrfProtectDeviceCodeRoutes()
    {
        await using var app = new TestApp();
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/providers/logins")).StatusCode);
        using var owner = await app.SignIn();
        Assert.Empty((await owner.GetFromJsonAsync<List<ProviderLogin>>("/api/v1/providers/logins"))!);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/runtimes/missing/providers/chatgpt", new RequestId(Guid.NewGuid().ToString()))).StatusCode);
    }

    [Fact]
    public async Task DuplicateStartsSharePendingFlowAndCompletionClearsCode()
    {
        await using var app = new TestApp();
        var runtime = new RuntimeRecord { Id = Guid.NewGuid().ToString("N"), DesiredConnected = true, Health = "Healthy", AllowedRoots = "/workspace" };
        await app.Store.Write(db => { db.Runtimes.Add(runtime); return Task.FromResult(true); });
        var native = new PendingHandler();
        var factory = new FakeFactory(native);
        var host = new FakeLifetime();
        var service = new ProviderLoginService(app.Store, factory, host, NullLogger<ProviderLoginService>.Instance);
        var first = await service.Start(runtime.Id, Guid.NewGuid().ToString());
        var repeated = await service.Start(runtime.Id, Guid.NewGuid().ToString());
        Assert.Equal(first.Id, repeated.Id);
        await TestApp.Wait(() => Task.FromResult(service.List().Single().State == "Waiting"), "device code");
        Assert.Equal("ABCD-E1234", service.List().Single().Code);
        native.Complete.TrySetResult();
        await TestApp.Wait(() => Task.FromResult(service.List().Single().State == "Connected"), "confirmed sign-in");
        Assert.Null(service.List().Single().Code);
        Assert.Null(service.List().Single().Url);
        Assert.Equal(1, factory.Connections);
        Assert.DoesNotContain(native.Paths, x => x.Contains("/session", StringComparison.Ordinal) || x.Contains("dispose", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InterruptedCallbackIsUnknownAndDoesNotLeakProviderErrors()
    {
        await using var app = new TestApp();
        var runtime = new RuntimeRecord { Id = Guid.NewGuid().ToString("N"), DesiredConnected = true, Health = "Healthy", AllowedRoots = "/workspace" };
        await app.Store.Write(db => { db.Runtimes.Add(runtime); return Task.FromResult(true); });
        var native = new PendingHandler();
        var service = new ProviderLoginService(app.Store, new FakeFactory(native), new FakeLifetime(), NullLogger<ProviderLoginService>.Instance);
        await service.Start(runtime.Id, Guid.NewGuid().ToString());
        await TestApp.Wait(() => Task.FromResult(service.List().Single().State == "Waiting"), "pending OAuth callback");
        native.Complete.TrySetException(new HttpRequestException("secret-provider-payload"));
        await TestApp.Wait(() => Task.FromResult(service.List().Single().State == "Unknown"), "uncertain result");
        Assert.DoesNotContain("secret-provider-payload", service.List().Single().Detail);
        Assert.Null(service.List().Single().Code);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class FakeFactory(PendingHandler handler) : IRuntimeTransportFactory
    {
        public int Connections { get; private set; }
        public Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken)
        {
            Connections++;
            return Task.FromResult<IRuntimeTransport>(new FakeTransport(new OpenCodeClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") })));
        }
    }
    private sealed class FakeTransport(OpenCodeClient api) : IRuntimeTransport
    {
        public OpenCodeClient Api => api;
        public bool Connected => true;
        public string Platform => "fixture";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { api.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (path.EndsWith("/callback", StringComparison.Ordinal)) await Complete.Task.WaitAsync(cancellationToken);
            var json = path switch
            {
                "/provider/auth" => """{"openai":[{"type":"oauth","label":"ChatGPT Pro/Plus (headless)"}]}""",
                "/provider/openai/oauth/authorize" => """{"url":"https://auth.openai.com/codex/device","method":"auto","instructions":"Enter code: ABCD-E1234"}""",
                "/provider/openai/oauth/callback" => "true",
                _ => throw new InvalidOperationException("Unexpected route")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
