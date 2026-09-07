using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeOnboardingTests
{
    [Fact]
    public async Task EncryptedCredentialsSurviveRestartAndRejectTampering()
    {
        string data, mounted, reference;
        const string password = "  entered-password-with-whitespace-0123456789\n";
        await using (var app = new TestApp())
        {
            data = app.DataPath; mounted = app.SecretPath;
            var secrets = app.Services.GetRequiredService<Secrets>();
            reference = secrets.StoreEncrypted(password);
            Assert.Equal(password, secrets.Read(reference));
            Assert.DoesNotContain(password.Trim(), File.ReadAllText(secrets.PathFor(reference)));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secrets.PathFor(reference)));
            var another = secrets.StoreEncrypted("another-test-secret");
            File.Copy(secrets.PathFor(reference), secrets.PathFor(another), true);
            Assert.Throws<ControlException>(() => secrets.Read(another));
            Assert.Throws<ControlException>(() => secrets.PathFor("vault-../../outside"));
        }
        await using var restored = new TestApp(data, mounted);
        Assert.Equal(password, restored.Services.GetRequiredService<Secrets>().Read(reference));
    }

    [Fact]
    public async Task VerificationEndpointsRequireAuthenticationCsrfAndUnmodifiedUnexpiredProof()
    {
        await using var app = new TestApp();
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        var profile = PersistenceTests.Profile();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/runtimes/verify", new RuntimeVerifyInput(profile))).StatusCode);
        using var client = await app.SignIn();
        var invalid = await client.PostAsJsonAsync("/api/v1/runtimes/verified-connect", new VerifiedRuntimeInput(profile, "invalid", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("HVO.AgentControl.RuntimeVerification.v1");
        var expired = protector.Protect(Json.Write(new { kind = "verified", binding = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Json.Write(profile)))), expires = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/runtimes/verified-connect", new VerifiedRuntimeInput(profile, expired, Guid.NewGuid().ToString()))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/runtimes/verify", new RuntimeVerifyInput(profile))).StatusCode);
        Assert.Empty((await app.Store.Snapshot()).Runtimes);
        profile = await app.Store.SaveRuntime(profile);
        var binding = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Json.Write(new { profile.Id, profile.Host, profile.Port, profile.Username }))));
        var oldEnrollment = protector.Protect(Json.Write(new { kind = "trust", binding, expires = ControlStore.Now + 60000, fingerprint = "SHA256:" + new string('B', 43), algorithm = "ssh-ed25519" }));
        var changedKey = await Assert.ThrowsAsync<ControlException>(() => app.Services.GetRequiredService<RuntimeVerificationService>().Verify(new(profile, "must-not-be-sent", TrustToken: oldEnrollment), CancellationToken.None));
        Assert.Contains("saved host key cannot be replaced", changedKey.Message);
    }

    [Fact]
    public async Task RegistrationAndConnectAreAtomicAndStartupValuesAreValidated()
    {
        await using var app = new TestApp();
        var profile = PersistenceTests.Profile();
        profile.LogLevel = "WARN; touch /tmp/forbidden";
        Assert.Throws<ControlException>(() => StartupOptions.Arguments(profile));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(profile));
        profile.LogLevel = "WARN"; profile.PureMode = true; profile.PrintLogs = true;
        Assert.Contains("serve --hostname 127.0.0.1 --port 4096 --pure --print-logs --log-level 'WARN'", BootstrapScript.Launcher(profile));
        var factory = app.Services.GetRequiredService<IDbContextFactory<ControlDb>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_connect BEFORE INSERT ON Commands BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => app.Store.SaveRuntimeAndConnect(profile, Guid.NewGuid().ToString()));
        Assert.Empty((await app.Store.Snapshot()).Runtimes);
    }

    [SshFact]
    public async Task PasswordEnrollmentPinsBeforeAuthenticationAndReconnectsWithEncryptedCredential()
    {
        var password = "onboarding-fixture-" + Guid.NewGuid().ToString("N");
        await SetPasswordAuthentication(password);
        var app = new TestApp(secrets: SshIntegrationTests.FixtureSecrets);
        RuntimeRecord? saved = null;
        try
        {
            var service = app.Services.GetRequiredService<RuntimeVerificationService>();
            var profile = SshIntegrationTests.Profile("a", "Password onboarding", Random.Shared.Next(56000, 60000));
            profile.Authentication = "password"; profile.CredentialReference = ""; profile.HostKeySha256 = "";
            profile.StateDirectory = ""; profile.AllowedRoots = ""; profile.ServerPasswordReference = "";
            var input = new RuntimeVerifyInput(profile);
            var discovered = await service.Verify(input, CancellationToken.None);
            Assert.Equal("TrustRequired", discovered.Status);
            Assert.NotEmpty(discovered.Profile.HostKeySha256);
            Assert.False(Directory.Exists(Path.Combine(app.DataPath, "credentials")));
            var retargeted = Json.Read<RuntimeRecord>(Json.Write(profile)); retargeted.Host = "example.invalid";
            await Assert.ThrowsAsync<ControlException>(() => service.Verify(new(retargeted, password, TrustToken: discovered.TrustToken), CancellationToken.None));
            var needed = await service.Verify(input with { TrustToken = discovered.TrustToken }, CancellationToken.None);
            Assert.Equal("CredentialsRequired", needed.Status);
            var bad = await service.Verify(input with { Password = "incorrect-password", TrustToken = discovered.TrustToken }, CancellationToken.None);
            Assert.Equal("CredentialsRequired", bad.Status);
            var result = await service.Verify(input with { Password = password, TrustToken = discovered.TrustToken }, CancellationToken.None);
            Assert.Equal("Verified", result.Status);
            Assert.All(result.Checks.Where(x => x.Required), x => Assert.True(x.Passed, x.Detail));
            Assert.Contains(result.Checks, x => x.Name == "gh" && !x.Required);
            Assert.Contains(result.Checks, x => x.Name == "az" && !x.Required);
            Assert.DoesNotContain(password, Json.Write(result));
            var secrets = app.Services.GetRequiredService<Secrets>();
            Assert.Equal(password, secrets.Read(result.Profile.CredentialReference));
            Assert.DoesNotContain(password, File.ReadAllText(secrets.PathFor(result.Profile.CredentialReference)));
            Assert.True(secrets.Read(result.Profile.ServerPasswordReference).Length >= 24);
            var changed = Json.Read<RuntimeRecord>(Json.Write(result.Profile)); changed.LogLevel = "WARN";
            await Assert.ThrowsAsync<ControlException>(() => service.SaveAndConnect(new(changed, result.VerificationToken!, Guid.NewGuid().ToString())));
            var connect = new VerifiedRuntimeInput(result.Profile, result.VerificationToken!, Guid.NewGuid().ToString());
            var receipts = await Task.WhenAll(service.SaveAndConnect(connect), service.SaveAndConnect(connect));
            Assert.Equal(receipts[0].Id, receipts[1].Id);
            saved = (await app.Store.Snapshot()).Runtimes.Single();
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Runtimes.Single().Health == "Healthy", "Encrypted password reconnect failed", 40);
            Assert.DoesNotContain(password, Json.Write(await app.Store.Snapshot()));
            using (var client = await app.SignIn()) Assert.DoesNotContain(password, await client.GetStringAsync("/api/v1/snapshot"));
            var data = app.DataPath; var secretPath = app.SecretPath;
            await app.DisposeAsync(); app = new TestApp(data, secretPath);
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Runtimes.Single().Health == "Healthy", "Stored credential did not survive restart", 40);
            Assert.Single((await app.Store.Snapshot()).Commands);
            var wrongKey = Json.Read<RuntimeRecord>(Json.Write(result.Profile)); wrongKey.Id = Guid.NewGuid().ToString("N"); wrongKey.HostKeySha256 = "SHA256:" + new string('A', 43);
            await Assert.ThrowsAsync<ControlException>(() => app.Services.GetRequiredService<RuntimeVerificationService>().Verify(new(wrongKey, password), CancellationToken.None));
        }
        finally
        {
            if (saved is not null)
                try { await using var connection = await app.Services.GetRequiredService<IRuntimeTransportFactory>().Connect(saved, CancellationToken.None); await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
            await app.DisposeAsync();
            await SetPasswordAuthentication(null);
        }
    }

    [SshFact]
    public async Task EnteredPrivateKeyIsProtectedAndMissingRequirementsAreReported()
    {
        await using var app = new TestApp(secrets: SshIntegrationTests.FixtureSecrets);
        var service = app.Services.GetRequiredService<RuntimeVerificationService>();
        var profile = SshIntegrationTests.Profile("a", "Key onboarding", Random.Shared.Next(56000, 60000));
        profile.CredentialReference = ""; profile.ServerPasswordReference = "";
        profile.AllowedRoots = "/home/agent/absent-" + Guid.NewGuid().ToString("N");
        var key = File.ReadAllText(Path.Combine(SshIntegrationTests.FixtureSecrets, "fixture-key"));
        var failed = await service.Verify(new(profile, PrivateKey: key), CancellationToken.None);
        Assert.Equal("RequirementsFailed", failed.Status);
        Assert.Contains(failed.Checks, x => !x.Passed && x.Name.StartsWith("Workspace:", StringComparison.Ordinal));
        Assert.NotNull(failed.SaveToken);
        Assert.Null(failed.VerificationToken);
        Assert.DoesNotContain(key, Json.Write(failed));
        Assert.Equal(key, app.Services.GetRequiredService<Secrets>().Read(failed.Profile.CredentialReference));
        var setup = new VerifiedRuntimeInput(failed.Profile, failed.SaveToken!, Guid.NewGuid().ToString());
        await Assert.ThrowsAsync<ControlException>(() => service.SaveAndConnect(setup));
        var tampered = Json.Read<RuntimeRecord>(Json.Write(failed.Profile)); tampered.Host = "another-host";
        await Assert.ThrowsAsync<ControlException>(() => service.SaveForSetup(setup with { Profile = tampered }));
        var first = await service.SaveForSetup(setup);
        Assert.Equal(first.Id, (await service.SaveForSetup(setup)).Id);
        var snapshot = await app.Store.Snapshot();
        profile = snapshot.Runtimes.Single();
        Assert.False(profile.DesiredConnected);
        Assert.Equal("Disconnected", profile.Transport);
        Assert.Equal("SetupRequired", profile.Health);
        Assert.DoesNotContain(snapshot.Commands, x => x.Kind == "EnsureServer");
        Assert.Single(snapshot.Commands, x => x.Kind == "SaveRuntimeForSetup");
        profile.AllowedRoots = "/home/agent/workspaces";
        var verified = await service.Verify(new(profile, PrivateKey: key), CancellationToken.None);
        Assert.Equal("Verified", verified.Status);
        Assert.DoesNotContain(key, Json.Write(verified));
        Assert.Equal(key, app.Services.GetRequiredService<Secrets>().Read(verified.Profile.CredentialReference));
        var reused = await service.Verify(new(verified.Profile), CancellationToken.None);
        Assert.Equal("Verified", reused.Status);
        Assert.Equal(verified.Profile.CredentialReference, reused.Profile.CredentialReference);
        var encryptedPath = Path.Combine(app.DataPath, "encrypted-test-key");
        File.WriteAllText(encryptedPath, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(encryptedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        const string phrase = "disposable-fixture-passphrase";
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "-p", "-P", "", "-N", phrase, "-f", encryptedPath }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); _ = await output; _ = await error; Assert.Equal(0, process.ExitCode);
        }
        var encryptedKey = File.ReadAllText(encryptedPath);
        await Assert.ThrowsAsync<ControlException>(() => service.Verify(new(profile, PrivateKey: encryptedKey), CancellationToken.None));
        var unlocked = await service.Verify(new(profile, PrivateKey: encryptedKey, Passphrase: phrase), CancellationToken.None);
        Assert.Equal("Verified", unlocked.Status);
        Assert.DoesNotContain(phrase, Json.Write(unlocked));
        Assert.Equal(phrase, app.Services.GetRequiredService<Secrets>().Read(unlocked.Profile.PassphraseReference!));
        Assert.Equal("Verified", (await service.Verify(new(unlocked.Profile), CancellationToken.None)).Status);
    }

    private static async Task SetPasswordAuthentication(string? password)
    {
        if (password is not null) await Docker("agent:" + password + "\n", "chpasswd");
        await Docker(null, "sh", "-c", password is null
            ? "rm -f /etc/ssh/sshd_config.d/00-hvo-onboarding.conf; passwd -d agent >/dev/null; kill -HUP $(cat /run/sshd.pid)"
            : "printf 'PasswordAuthentication yes\\n' > /etc/ssh/sshd_config.d/00-hvo-onboarding.conf; kill -HUP $(cat /run/sshd.pid)");
    }
    private static async Task Docker(string? input, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "-i", "hvo-agentcontrol-fixture-a" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close(); await process.WaitForExitAsync();
        _ = await output; _ = await error;
        Assert.Equal(0, process.ExitCode);
    }
}
