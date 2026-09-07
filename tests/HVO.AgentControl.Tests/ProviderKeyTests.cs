using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderKeyTests
{
    private const string Key = "fixture-go-key-never-return";

    [Fact]
    public async Task SavesEncryptedSurvivesRestartAndNeverReturnsKeyOrReference()
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-key-" + Guid.NewGuid().ToString("N"));
        await using (var app = new TestApp(data))
        {
            using var anonymous = app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/providers/opencode-go/key")).StatusCode);
            using var owner = await app.SignIn();
            var response = await owner.PostAsJsonAsync("/api/v1/providers/opencode-go/key", new SaveProviderKey(Key, 0));
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Key, text); Assert.DoesNotContain("secretReference", text, StringComparison.OrdinalIgnoreCase);
            var reference = await app.Store.Read(async db => (await db.Set<ProviderCredential>().SingleAsync()).SecretReference);
            var secrets = app.Services.GetRequiredService<Secrets>();
            Assert.DoesNotContain(Key, await File.ReadAllTextAsync(secrets.PathFor(reference)));
            Assert.DoesNotContain(Key, Json.Write(await app.Store.Read(db => db.Events.ToListAsync())));
            Assert.Equal(Key, secrets.Read(reference));
            Assert.False((await owner.PostAsJsonAsync("/api/v1/providers/opencode-go/key", new SaveProviderKey("replacement", 0))).IsSuccessStatusCode);
            owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Assert.False((await owner.PostAsJsonAsync("/api/v1/providers/opencode-go/key", new SaveProviderKey("replacement", 1))).IsSuccessStatusCode);
        }
        await using (var restarted = new TestApp(data))
        {
            var status = await restarted.Services.GetRequiredService<ProviderKeyService>().Status();
            Assert.True(status.Saved); Assert.Equal(1, status.Revision);
            var reference = await restarted.Store.Read(async db => (await db.Set<ProviderCredential>().SingleAsync()).SecretReference);
            Assert.Equal(Key, restarted.Services.GetRequiredService<Secrets>().Read(reference));
        }
    }

    [Theory]
    [InlineData(false, "StoredOnRuntime")]
    [InlineData(true, "Unconfirmed")]
    public async Task DeliveryUsesOnlyAuthEndpointAndPersistsUncertainOutcomes(bool fail, string expected)
    {
        await using var app = new TestApp();
        var runtime = new RuntimeRecord { DesiredConnected = true, Health = "Healthy" };
        await app.Store.Write(db => { db.Runtimes.Add(runtime); return Task.FromResult(true); });
        var native = new Handler(fail);
        var service = new ProviderKeyService(app.Store, app.Services.GetRequiredService<Secrets>(), new Factory(native));
        await service.Save(new(Key, 0));
        var status = await service.Apply(runtime.Id, new(1), CancellationToken.None);
        Assert.Equal(expected, Assert.Single(status.Deliveries).State);
        Assert.Equal(1, native.Calls);
        Assert.DoesNotContain(Key, Json.Write(status));
        Assert.DoesNotContain(Key, Json.Write(await app.Store.Read(db => db.Events.ToListAsync())));
        Assert.Empty((await app.Store.Snapshot()).Commands);
        await service.Save(new("rotated-fixture-key", 1));
        await Assert.ThrowsAsync<ControlException>(() => service.Apply(runtime.Id, new(1), CancellationToken.None));
        Assert.Equal(1, native.Calls);
        Assert.Equal(1, Assert.Single((await service.Status()).Deliveries).KeyRevision);
    }

    private sealed class Handler(bool fail) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/auth/opencode-go", request.RequestUri!.PathAndQuery);
            Assert.Equal(Json.Write(new { type = "api", key = Key }), await request.Content!.ReadAsStringAsync(cancellationToken));
            if (fail) throw new HttpRequestException(Key);
            return new(HttpStatusCode.OK) { Content = new StringContent("true") };
        }
    }
    private sealed class Factory(Handler handler) : IRuntimeTransportFactory
    {
        public Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken) =>
            Task.FromResult<IRuntimeTransport>(new Transport(new(new HttpClient(handler) { BaseAddress = new("http://localhost") })));
    }
    private sealed class Transport(OpenCodeClient api) : IRuntimeTransport
    {
        public OpenCodeClient Api => api;
        public bool Connected => true;
        public string Platform => "fixture";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { api.Dispose(); return ValueTask.CompletedTask; }
    }
}
