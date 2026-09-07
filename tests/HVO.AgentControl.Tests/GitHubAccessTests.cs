using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class GitHubAccessTests
{
    private sealed class GitHubCliFactAttribute : FactAttribute
    {
        public GitHubCliFactAttribute()
        { if (Environment.GetEnvironmentVariable("HVO_GITHUB_CLI_TESTS") != "1") Skip = "Set HVO_GITHUB_CLI_TESTS=1 with GitHub CLI installed."; }
    }

    [GitHubCliFact]
    public async Task InstalledCliReadsCredentialWithoutUserLookupMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-gh-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), GitHubCredentialDelivery.HostsYaml(new("fixture-token-format-only", Now.AddHours(1), "agentcontrol-test[bot]")));
            var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "auth", "token", "--hostname", "github.com" }) start.ArgumentList.Add(argument);
            foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_HOST", "GH_DEBUG" }) start.Environment.Remove(name);
            start.Environment["GH_CONFIG_DIR"] = directory;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            var value = await output; _ = await error;
            // Never include returned credential text in test diagnostics, even on a failure.
            Assert.True(process.ExitCode == 0 && value.Trim() == "fixture-token-format-only", "GitHub CLI did not read the isolated fixture credential.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(value)) };
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private static object Grant(string[]? repositories = null, Dictionary<string, string>? permissions = null, int expiryMinutes = 60) => new
    {
        token = "installation-test-secret",
        expires_at = Now.AddMinutes(expiryMinutes),
        repository_selection = "selected",
        repositories = (repositories ?? ["Owner/Repo"]).Select(x => new { full_name = x }),
        permissions = permissions ?? new() { ["contents"] = "write", ["issues"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" }
    };

    [Fact]
    public async Task JwtIsSignedAndIssuanceExplicitlyRestrictsRepositoryAndPermissions()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new Handler(async request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var jwt = request.Headers.Authorization!.Parameter!.Split('.');
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var claims = JsonDocument.Parse(Decode(jwt[1]));
            Assert.Equal("123", claims.RootElement.GetProperty("iss").GetString());
            Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
            Assert.Equal(Now.AddSeconds(540).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
            if (request.Method == HttpMethod.Get) return Response(new { account = new { login = "Owner" }, app_slug = "agentcontrol-test" });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Repo", Assert.Single(body.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
            Assert.Equal(3, body.RootElement.GetProperty("permissions").EnumerateObject().Count());
            Assert.Equal("write", body.RootElement.GetProperty("permissions").GetProperty("pull_requests").GetString());
            return Response(Grant());
        });
        using var http = new HttpClient(handler);
        var token = await new GitHubAppClient(http, new Clock()).Issue(123, 456, rsa.ExportRSAPrivateKeyPem(), ["Owner/Repo"], CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(Now.AddHours(1), token.ExpiresAt);
        Assert.DoesNotContain(token.Value, token.ToString());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("extra-repository")]
    [InlineData("missing-repository")]
    [InlineData("extra-permission")]
    [InlineData("missing-permission")]
    [InlineData("expiry")]
    [InlineData("forbidden")]
    public async Task UnexpectedScopeOrErrorsNeverProduceCredentials(string scenario)
    {
        using var rsa = RSA.Create(2048);
        using var handler = new Handler(request =>
        {
            if (scenario == "forbidden") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive-upstream-response") });
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(new { account = new { login = scenario == "account" ? "Other" : "Owner" }, app_slug = "agentcontrol-test" }));
            var permissions = new Dictionary<string, string> { ["contents"] = "write", ["issues"] = "write", ["pull_requests"] = "write" };
            if (scenario == "extra-permission") permissions["administration"] = "write";
            if (scenario == "missing-permission") permissions["issues"] = "read";
            return Task.FromResult(Response(Grant(scenario switch { "extra-repository" => ["Owner/Repo", "Owner/Other"], "missing-repository" => [], _ => null }, permissions, scenario == "expiry" ? 1 : 60)));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ControlException>(() => new GitHubAppClient(http, new Clock()).Issue(1, 2, rsa.ExportRSAPrivateKeyPem(), ["Owner/Repo"], CancellationToken.None));
        Assert.DoesNotContain("sensitive-upstream-response", error.Message);
        Assert.DoesNotContain("installation-test-secret", error.Message);
        if (scenario is "account" or "forbidden") Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/Owner/Repo")]
    [InlineData("Owner/../Repo")]
    [InlineData("Owner/Repo\nmalicious")]
    public void InvalidRepositoryInputIsRejected(string repository) => Assert.Throws<ControlException>(() => GitHubAppClient.ValidateRepositories([repository]));

    [Fact]
    public async Task ConfigurationPersistsEncryptedKeyButNeverReturnsOrJournalsCredentials()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        using var handler = new Handler(request => Task.FromResult(Response(request.Method == HttpMethod.Get ? new { account = new { login = "Owner" }, app_slug = "agentcontrol-test" } : Grant())));
        using var http = new HttpClient(handler);
        var secrets = app.Services.GetRequiredService<Secrets>();
        var service = new GitHubAccessService(app.Store, secrets, new GitHubAppClient(http, new Clock()));
        var saved = await service.Configure(runtime.Id, new(1, 2, pem, ["Owner/Repo"], 0), CancellationToken.None);
        Assert.Equal(pem, secrets.Read(saved.PrivateKeyReference));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(secrets.PathFor(saved.PrivateKeyReference)));
        using var owner = await app.SignIn();
        var response = await owner.GetStringAsync("/api/v1/github/access");
        Assert.DoesNotContain("privateKey", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installation-test-secret", response);
        var conflict = await Assert.ThrowsAsync<ControlException>(() => service.Configure(runtime.Id, new(1, 2, "", ["Owner/Repo"], 0), CancellationToken.None));
        Assert.Contains("changed", conflict.Message);
        var updated = await service.Configure(runtime.Id, new(1, 2, "", ["Owner/Repo"], saved.Revision), CancellationToken.None);
        Assert.Equal(saved.PrivateKeyReference, updated.PrivateKeyReference);
        var redirected = Json.Read<RuntimeRecord>(Json.Write(runtime)); redirected.Host = "other-host";
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(redirected));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteRuntime(runtime.Id, new(Guid.NewGuid().ToString(), runtime.Revision)));
        var disabled = await service.Disable(runtime.Id, updated.Revision, CancellationToken.None);
        Assert.Equal("Disabled", disabled.State);
        await Assert.ThrowsAsync<ControlException>(() => service.Disable(runtime.Id, updated.Revision, CancellationToken.None));
        Assert.Equal("other-host", (await app.Store.SaveRuntime(redirected)).Host);
        await app.Store.Write(async db =>
        {
            var grant = (await db.GitHubAccess.FindAsync(runtime.Id))!;
            grant.State = "Ready"; grant.ExpiresAt = ControlStore.Now - 1000;
            return true;
        });
        Assert.Equal("Expired", Assert.Single(await service.List()).State);
        var events = await app.Store.Read(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Events));
        Assert.All(events, e => { Assert.DoesNotContain("PRIVATE KEY", e.Payload); Assert.DoesNotContain("installation-test-secret", e.Payload); });
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/github/access")).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/v1/runtimes/" + runtime.Id + "/github", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [SshFact]
    public async Task DeliveryRotatesOwnedFileAndPreservesPersonalLogin()
    {
        var runtime = SshIntegrationTests.Profile("b", "GitHub delivery", 19998);
        var secrets = new Secrets(Options.Create(new ControlOptions { SecretsDirectory = SshIntegrationTests.FixtureSecrets }));
        var delivery = new GitHubCredentialDelivery(secrets);
        async Task<string> Docker(string script)
        {
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "hvo-agentcontrol-fixture-b", "sh", "-c", script }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        await Docker("test ! -e /home/agent/.config/gh && printf '#!/bin/sh\\nexit 0\\n' > /usr/local/bin/gh && chmod 755 /usr/local/bin/gh");
        try
        {
            await delivery.Deliver(runtime, new("fixture-token-one", Now.AddHours(1)), CancellationToken.None);
            var first = await Docker("cat /home/agent/.config/gh/hosts.yml; stat -c '%a' /home/agent/.config/gh/hosts.yml");
            Assert.Contains("fixture-token-one", first); Assert.Contains("600", first);
            await delivery.Deliver(runtime, new("fixture-token-two", Now.AddHours(1)), CancellationToken.None);
            var second = await Docker("cat /home/agent/.config/gh/hosts.yml");
            Assert.Contains("fixture-token-two", second); Assert.DoesNotContain("fixture-token-one", second);
            await Docker("rm /home/agent/.config/gh/.agentcontrol-owner; printf 'personal-login' > /home/agent/.config/gh/hosts.yml");
            await Assert.ThrowsAsync<ControlException>(() => delivery.Deliver(runtime, new("must-not-replace", Now.AddHours(1)), CancellationToken.None));
            Assert.Equal("personal-login", await Docker("cat /home/agent/.config/gh/hosts.yml"));
        }
        finally { await Docker("rm -f /home/agent/.config/gh/hosts.yml /home/agent/.config/gh/.agentcontrol-owner /usr/local/bin/gh; rmdir /home/agent/.config/gh"); }
    }
}
