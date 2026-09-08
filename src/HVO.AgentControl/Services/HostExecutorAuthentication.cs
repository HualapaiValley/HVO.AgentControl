using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.Services;

public static class HostExecutorAuthenticationDefaults
{
    public const string Scheme = "HostExecutorBearer";
    public const string EnrollmentPolicy = "HostExecutorEnrollment";
    public const string ActivePolicy = "ActiveHostExecutor";
    public const string HostIdClaim = "hvo:host-id";
    public const string GenerationClaim = "hvo:authority-generation";
    public const string StateClaim = "hvo:host-executor-state";
    public const string CredentialDigestClaim = "hvo:presented-credential-digest";
}

public sealed class HostExecutorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<ControlDb> factory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Failed();
        var token = header[7..];
        var separator = token.IndexOf('.');
        if (token.Length > 200 || separator <= 0 || separator != token.LastIndexOf('.') ||
            !Guid.TryParse(token[..separator], out var parsedId) || parsedId == Guid.Empty ||
            !Regex.IsMatch(token[(separator + 1)..], "^[A-Za-z0-9_-]{43}$"))
            return Failed();

        var id = parsedId.ToString("N");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token[(separator + 1)..])));
        await using var db = await factory.CreateDbContextAsync();
        var enrollment = await db.HostExecutors.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (enrollment is null || enrollment.State is HostExecutorState.Suspended or HostExecutorState.Revoked ||
            !FixedEquals(enrollment.CredentialDigest, digest))
            return Failed();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, enrollment.Id),
            new Claim(HostExecutorAuthenticationDefaults.HostIdClaim, enrollment.HostId),
            new Claim(HostExecutorAuthenticationDefaults.GenerationClaim, enrollment.AuthorityGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(HostExecutorAuthenticationDefaults.StateClaim, enrollment.State),
            new Claim(HostExecutorAuthenticationDefaults.CredentialDigestClaim, digest)
        };
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, HostExecutorAuthenticationDefaults.Scheme)),
            HostExecutorAuthenticationDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private static AuthenticateResult Failed() => AuthenticateResult.Fail("Host executor authentication failed.");

    private static bool FixedEquals(string expected, string presented)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(presented)); }
        catch (FormatException) { return false; }
    }
}
