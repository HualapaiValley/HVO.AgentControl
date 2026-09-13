using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;

namespace HVO.AgentControl.Services;

public sealed class ControlServiceRegistration(ControlStore store, Secrets secrets)
{
    public async Task<ControlServiceRecord> Register(RegisterControlServiceInput input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.PasswordReference)) throw new ControlException("Choose the mounted control service password reference.", 400);
        using var api = ControlHttpTransport.CreateClient(input.Endpoint, secrets.Read(input.PasswordReference));
        var identity = await ControlHttpTransport.ReadIdentity(api, input.ExpectedInstanceId, token);
        await api.Verify(token);
        return await store.RegisterControlService(input, identity);
    }
}
