using System.Net.Http.Headers;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.OpenCode;

public sealed class RuntimeTransportFactory(SshRuntimeTransportFactory ssh, ControlStore store, Secrets secrets) : IRuntimeTransportFactory
{
    public async Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken token)
    {
        if (runtime.ConnectionKind == RuntimeConnections.Ssh) return await ssh.Connect(runtime, token);
        if (runtime.ConnectionKind != RuntimeConnections.ControlHttp) throw new ControlException("Unsupported runtime connection kind.");
        var service = await store.Read(db => db.ControlServices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runtime.Id, token))
            ?? throw new ControlException("Control service registration is missing.");
        var api = ControlHttpTransport.CreateClient(service.Endpoint, secrets.Read(runtime.ServerPasswordReference));
        var transport = new ControlHttpTransport(api, service, store);
        try { await transport.ValidateConnection(token); return transport; }
        catch { await transport.DisposeAsync(); throw; }
    }
}

public sealed class ControlHttpTransport(OpenCodeClient api, ControlServiceRecord service, ControlStore store) : IRuntimeTransport
{
    private string? verifiedIncarnation;
    public OpenCodeClient Api => api;
    public bool Connected => true; // HTTP health/SSE observation establishes reachability; no invented SSH process.
    public string Platform => "OpenCode control sidecar";
    public async Task<RuntimeProcessIdentity?> ProbeProcessIdentity(CancellationToken token)
    {
        var identity = await ReadIdentity(api, service.InstanceId, token);
        return new(RuntimeConnections.ControlHttp, identity.InstanceId, identity.IncarnationId, null, ControlStore.Now);
    }
    public async Task ValidateConnection(CancellationToken token)
    {
        var identity = await ReadIdentity(api, service.InstanceId, token);
        // Provider delivery also uses this factory, so validate before returning a connection.
        // A replacement process must re-establish its schema before any dispatch resumes.
        if (identity.IncarnationId != verifiedIncarnation)
        {
            await api.Verify(token);
            verifiedIncarnation = identity.IncarnationId;
        }
        else await api.VerifyHealth(token);
        await store.ObserveControlService(service.Id, identity);
    }
    public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken token) =>
        throw new ControlException("Control services do not provision development workspaces.");
    public Task StopOwnedServer(CancellationToken token) =>
        throw new ControlException("The container owner manages this service's lifecycle. Disconnecting the host never stops OpenCode.");
    public ValueTask DisposeAsync() { api.Dispose(); return ValueTask.CompletedTask; }

    public static OpenCodeClient CreateClient(string endpoint, string password)
    {
        if (endpoint is null || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ControlException("Enter the control service HTTP(S) origin without a path, credentials, query, or fragment.", 400);
        if (password.Length is < 32 or > 256 || password.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=' or '_' or '-')))
            throw new ControlException("Control service password must match the sidecar's 32–256 character secret.", 400);
        // Never forward this credential to an HTTP redirect destination or an environment proxy.
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        return new OpenCodeClient(http);
    }

    public static async Task<ControlServiceIdentity> ReadIdentity(OpenCodeClient client, string expectedInstanceId, CancellationToken token)
    {
        if (!Guid.TryParse(expectedInstanceId, out _)) throw new ControlException("Supply the instance ID from the owned sidecar's identity manifest.", 400);
        var file = await client.Get(OpenCodeClient.Scope("/file/content?path=.agentcontrol-service.json", ControlStore.ControlDirectory), token, 8192);
        if (file.GetProperty("type").GetString() != "text") throw new ControlException("Control service identity is not a text manifest.");
        var identity = Json.Read<ControlServiceIdentity>(file.GetProperty("content").GetString() ?? "");
        if (identity.SchemaVersion != 1 || identity.InstanceId != expectedInstanceId || !Guid.TryParse(identity.IncarnationId, out _) ||
            identity.Directory != ControlStore.ControlDirectory || !DateTimeOffset.TryParse(identity.StartedAt, out _))
            throw new ControlException("Control service identity, directory, or manifest version does not match its registration.");
        return identity;
    }
}
