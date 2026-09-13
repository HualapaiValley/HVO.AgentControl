using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace HVO.AgentControl.Services;

public sealed class OwnerAuthenticationStateProvider(ILoggerFactory logger) : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var expires = authenticationState.User.FindFirst("hvo:expires")?.Value;
        return Task.FromResult(long.TryParse(expires, out var timestamp) && timestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
