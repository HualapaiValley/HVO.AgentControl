using System.Diagnostics;
using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class GitHubProcessEnvironmentTests
{
    [Fact]
    public async Task LegacyReadyGrantMigratesAsUnknownAndProjectsMigrationRequired()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-github-readiness-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={Path.Combine(directory, "agentcontrol.db")}").Options;
        try
        {
            await using (var db = new ControlDb(options))
            {
                var previous = db.Database.GetMigrations().TakeWhile(x => !x.EndsWith("GitHubProcessEnvironmentReadiness", StringComparison.Ordinal)).Last();
                await db.GetService<IMigrator>().MigrateAsync(previous);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO GitHubAccess
                    (Id, AppId, InstallationId, PrivateKeyReference, RepositoriesJson, State, Detail, ExpiresAt, Revision, RetryAt,
                     ChecksPermission, CommitStatusesPermission, ActionsPermission, PermissionsVerifiedAt)
                    VALUES ('legacy', 1, 2, 'key', '["Owner/Repo"]', 'Ready', 'Previously ready', 9999999999999, 4, 0,
                            'Granted', 'Granted', 'Denied', 1)
                    """);
                await db.Database.MigrateAsync();
            }
            await using (var migrated = new ControlDb(options))
            {
                var legacy = await migrated.GitHubAccess.AsNoTracking().SingleAsync();
                Assert.Equal("Ready", legacy.State);
                Assert.Equal(GitHubCredentialState.Unknown, legacy.CredentialState);
                Assert.Empty(legacy.CredentialConfigurationFingerprint);
                Assert.Equal(0, legacy.EnvironmentPolicyVersion);
                Assert.Null(legacy.EnvironmentProcessId);
                Assert.Equal("CredentialUnavailable", legacy.ExactCiInspectionState);
            }
        }
        finally { Directory.Delete(directory, true); }

        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        await app.Store.Write(db =>
        {
            db.GitHubAccess.Add(new()
            {
                Id = runtime.Id,
                State = "Ready",
                CredentialState = GitHubCredentialState.Unknown,
                ExpiresAt = ControlStore.Now + 3600000,
                ChecksPermission = GitHubPermissionState.Granted,
                CommitStatusesPermission = GitHubPermissionState.Granted
            });
            return Task.FromResult(true);
        });
        var projected = Assert.Single(await app.Services.GetRequiredService<GitHubAccessService>().List());
        Assert.Equal("MigrationRequired", projected.State);
        Assert.Equal("CredentialUnavailable", projected.ExactCiInspectionState);
    }

    [Fact]
    public async Task ReadyProjectionRejectsStaleRuntimeAndReplacementProcessEvidence()
    {
        await using var app = new TestApp();
        var runtime = PersistenceTests.Profile();
        runtime.DesiredConnected = true; runtime.Health = "Healthy";
        runtime = await app.Store.SaveRuntime(runtime);
        var now = ControlStore.Now;
        await app.Store.Write(db =>
        {
            db.GitHubAccess.Add(new()
            {
                Id = runtime.Id,
                State = "Ready",
                CredentialState = GitHubCredentialState.Delivered,
                CredentialConfigurationFingerprint = new string('b', 64),
                ExpiresAt = now + 3600000,
                ChecksPermission = GitHubPermissionState.Granted,
                CommitStatusesPermission = GitHubPermissionState.Granted,
                EnvironmentPolicyVersion = GitHubProcessEnvironment.CurrentPolicyVersion,
                EnvironmentPolicyFingerprint = new string('a', 64),
                EnvironmentProcessId = 42,
                EnvironmentProcessIncarnation = "boot:42",
                EnvironmentVerifiedAt = now
            });
            return Task.FromResult(true);
        });
        var service = app.Services.GetRequiredService<GitHubAccessService>();
        Assert.Equal("MigrationRequired", Assert.Single(await service.List()).State);

        await app.Store.Write(async db =>
        {
            var grant = (await db.GitHubAccess.FindAsync(runtime.Id))!;
            grant.EnvironmentPolicyFingerprint = GitHubProcessEnvironment.Fingerprint(runtime);
            return true;
        });
        Assert.Equal("Ready", Assert.Single(await service.List()).ExactCiInspectionState);

        await app.Store.ObserveNativeProcess(runtime.Id, new(runtime.ManagedServerId,
            NativeProcessObservationState.Observed, "Linux", 43, "boot:43", ControlStore.Now,
            "SyntheticTest", "replacement", []));
        var replaced = Assert.Single(await service.List());
        Assert.Equal("MigrationRequired", replaced.State);
        Assert.Equal("CredentialUnavailable", replaced.ExactCiInspectionState);
    }

    [Fact]
    public async Task EnvironmentCommitRejectsANewerContradictoryNativeObservation()
    {
        await using var app = new TestApp();
        var runtime = PersistenceTests.Profile();
        runtime.DesiredConnected = true; runtime.Health = "Healthy";
        runtime = await app.Store.SaveRuntime(runtime);
        var now = ControlStore.Now;
        GitHubAccess grant = null!;
        await app.Store.Write(db =>
        {
            grant = new GitHubAccess
            {
                Id = runtime.Id,
                State = "MigrationRequired",
                CredentialState = GitHubCredentialState.Delivered,
                CredentialConfigurationFingerprint = new string('b', 64),
                ExpiresAt = now + 3600000
            };
            db.GitHubAccess.Add(grant);
            return Task.FromResult(true);
        });
        var environment = new GitHubProcessEnvironmentEvidence(GitHubProcessEnvironment.Ready, "Linux", 42,
            "boot:42", now, GitHubProcessEnvironment.CurrentPolicyVersion,
            GitHubProcessEnvironment.Fingerprint(runtime), grant.CredentialConfigurationFingerprint);
        await app.Store.ObserveNativeProcess(runtime.Id, new(runtime.ManagedServerId,
            NativeProcessObservationState.Observed, "Linux", 43, "boot:43", now,
            "SyntheticTest", "replacement", []));
        var services = app.Services;
        var supervisor = new GitHubCredentialSupervisor(app.Store, services.GetRequiredService<Secrets>(),
            services.GetRequiredService<GitHubAppClient>(), services.GetRequiredService<GitHubAccessService>(),
            services.GetRequiredService<GitHubCredentialDelivery>());
        Assert.False(await supervisor.SaveEnvironment(grant, runtime, environment, null, false));
        Assert.Equal("MigrationRequired", (await app.Store.Read(async db => await db.GitHubAccess.FindAsync(runtime.Id)))!.State);
    }

    [Fact]
    public void CurrentPolicyRequiresExactFreshProcessEvidenceAndRejectsMalformedProbeOutput()
    {
        var runtime = PersistenceTests.Profile();
        runtime.StateDirectory = "/home/agent/state-managed";
        var grant = new GitHubAccess
        {
            State = "Ready",
            CredentialState = GitHubCredentialState.Delivered,
            CredentialConfigurationFingerprint = new string('b', 64),
            ExpiresAt = ControlStore.Now + 3600000,
            EnvironmentPolicyVersion = GitHubProcessEnvironment.CurrentPolicyVersion,
            EnvironmentPolicyFingerprint = GitHubProcessEnvironment.Fingerprint(runtime),
            EnvironmentProcessId = 42,
            EnvironmentProcessIncarnation = "boot:42",
            EnvironmentVerifiedAt = ControlStore.Now
        };
        Assert.False(GitHubProcessEnvironment.MustVerify(grant, runtime, ControlStore.Now));
        grant.State = "MigrationRequired";
        Assert.True(GitHubProcessEnvironment.MustVerify(grant, runtime, ControlStore.Now));
        grant.State = "Ready"; grant.EnvironmentPolicyFingerprint = "different";
        Assert.True(GitHubProcessEnvironment.MustVerify(grant, runtime, ControlStore.Now));
        grant.EnvironmentPolicyFingerprint = GitHubProcessEnvironment.Fingerprint(runtime);
        grant.EnvironmentVerifiedAt = ControlStore.Now - GitHubProcessEnvironment.RecheckMilliseconds - 1;
        Assert.True(GitHubProcessEnvironment.MustVerify(grant, runtime, ControlStore.Now));

        grant.EnvironmentVerifiedAt = ControlStore.Now;
        Assert.True(GitHubProcessEnvironment.HasCurrentEvidence(grant, ControlStore.Now));
        Assert.False(GitHubProcessEnvironment.HasCurrentEvidence(grant,
            ControlStore.Now + GitHubProcessEnvironment.RecheckMilliseconds + 1));
        grant.CredentialState = GitHubCredentialState.Unknown;
        Assert.False(GitHubProcessEnvironment.HasCurrentEvidence(grant, ControlStore.Now));
        grant.CredentialState = GitHubCredentialState.Delivered;
        grant.ExpiresAt = ControlStore.Now - 1;
        grant.ChecksPermission = GitHubPermissionState.Granted;
        grant.CommitStatusesPermission = GitHubPermissionState.Granted;
        Assert.Equal("CredentialUnavailable", grant.ExactCiInspectionState);
        grant.ExpiresAt = ControlStore.Now + 3600000;

        var replacement = new NativeProcessObservationEvidence(runtime.Id, runtime.ManagedServerId,
            NativeProcessObservationState.Observed, "Linux", 43, "boot:43", ControlStore.Now, NativeProcessProbe.Provenance,
            "fresh replacement", "Fresh");
        Assert.True(GitHubProcessEnvironment.NewerNativeProcessContradicts(grant, replacement));
        Assert.False(GitHubProcessEnvironment.NewerNativeProcessContradicts(grant,
            replacement with { ObservedAt = ControlStore.Now - 60001 }));

        var malformed = GitHubProcessEnvironment.Parse(runtime, "GITHUB_ENVIRONMENT\tReady\tLinux\t42\tboot:42\nextra\n",
            ControlStore.Now, new string('a', 64));
        Assert.Equal(GitHubProcessEnvironment.Unavailable, malformed.Status);
        Assert.Null(malformed.ProcessId);
        Assert.DoesNotContain("extra", malformed.Detail);
    }

    [Fact]
    public void StoredCredentialAndProcessReadinessHaveIndependentRenewalDecisions()
    {
        var now = ControlStore.Now;
        var grant = new GitHubAccess
        {
            State = "MigrationRequired",
            CredentialState = GitHubCredentialState.Delivered,
            ExpiresAt = now + 3600000
        };
        Assert.False(GitHubCredentialSupervisor.CredentialNeedsDelivery(grant, now));
        grant.ExpiresAt = now + 600000;
        Assert.True(GitHubCredentialSupervisor.CredentialNeedsDelivery(grant, now));
        grant.ExpiresAt = now + 3600000; grant.CredentialState = GitHubCredentialState.Unknown;
        Assert.True(GitHubCredentialSupervisor.CredentialNeedsDelivery(grant, now));
    }

    [Theory]
    [InlineData("ConfigMissing", "MigrationRequired")]
    [InlineData("ConfigMismatch", "MigrationRequired")]
    [InlineData("TokenOverride", "MigrationRequired")]
    [InlineData("CredentialMismatch", "Blocked")]
    [InlineData("Unavailable", "Blocked")]
    public void ProcessProbeReasonsRemainBoundedAndNeverContainEnvironmentValues(string status, string state)
    {
        var runtime = PersistenceTests.Profile();
        var evidence = GitHubProcessEnvironment.Parse(runtime,
            $"GITHUB_ENVIRONMENT\t{status}\tLinux\t42\tboot:42\n", ControlStore.Now, new string('a', 64));
        Assert.Equal(state, evidence.State);
        Assert.DoesNotContain("GH_TOKEN=", evidence.Detail);
        Assert.DoesNotContain("GITHUB_TOKEN=", evidence.Detail);
        Assert.True(evidence.Detail.Length < 500);
    }

    [SshFact]
    public async Task ExistingProcessStaysMigrationRequiredUntilAnExplicitOwnedStopAndFreshStart()
    {
        var runtime = SshIntegrationTests.Profile("a", "GitHub environment migration", Random.Shared.Next(41000, 50000));
        runtime.StateDirectory = "/home/agent/github-environment-" + Guid.NewGuid().ToString("N");
        var secrets = new Secrets(Options.Create(new ControlOptions { SecretsDirectory = SshIntegrationTests.FixtureSecrets }));
        var factory = new SshRuntimeTransportFactory(secrets);
        var delivery = new GitHubCredentialDelivery(secrets);
        var personal = "/home/agent/.config/gh/hosts.yml";
        IRuntimeTransport? active = null;

        await Docker("root", "sh", "-c", "rm -rf " + BootstrapScript.Quote(runtime.StateDirectory) +
            " /home/agent/.config/gh; printf '#!/bin/sh\\nexit 0\\n' > /usr/local/bin/gh; chmod 755 /usr/local/bin/gh");
        await Docker("agent", "sh", "-c", "umask 077; mkdir -p /home/agent/.config/gh; printf personal-login > /home/agent/.config/gh/hosts.yml");
        try
        {
            active = await factory.Connect(runtime, CancellationToken.None);
            await StopActive();

            var password = secrets.Read(runtime.ServerPasswordReference);
            var legacyEnvironment = "export OPENCODE_SERVER_USERNAME=opencode\n" +
                "export OPENCODE_SERVER_PASSWORD=" + BootstrapScript.Quote(password) + "\n";
            await DockerWrite(runtime.StateDirectory + "/server.env", legacyEnvironment);
            await Docker("agent", "/bin/sh", runtime.StateDirectory + "/ensure.sh");

            var legacy = await delivery.Deliver(runtime,
                new("fixture-token-review-only", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);
            Assert.Equal(GitHubProcessEnvironment.ConfigMissing, legacy.Status);
            Assert.Equal("MigrationRequired", legacy.State);
            Assert.Equal("personal-login", (await Docker("agent", "cat", personal)).Trim());
            Assert.DoesNotContain("fixture-token-review-only", Json.Write(legacy));

            active = await factory.Connect(runtime, CancellationToken.None);
            var reconnected = await delivery.VerifyEnvironment(runtime, legacy.CredentialConfigurationFingerprint, CancellationToken.None);
            Assert.Equal(legacy.ProcessId, reconnected.ProcessId);
            Assert.Equal(legacy.ProcessIncarnation, reconnected.ProcessIncarnation);
            Assert.Equal(GitHubProcessEnvironment.ConfigMissing, reconnected.Status);
            await StopActive();

            const string overrideValue = "fixture-process-override-never-reported";
            await DockerWrite(runtime.StateDirectory + "/server.env", BootstrapScript.ServerEnvironment(runtime, password) +
                "export GITHUB_TOKEN=" + BootstrapScript.Quote(overrideValue) + "\n");
            await Docker("agent", "/bin/sh", runtime.StateDirectory + "/ensure.sh");
            var overridden = await delivery.VerifyEnvironment(runtime, legacy.CredentialConfigurationFingerprint, CancellationToken.None);
            Assert.Equal(GitHubProcessEnvironment.TokenOverride, overridden.Status);
            Assert.DoesNotContain(overrideValue, overridden.Detail);
            Assert.DoesNotContain(overrideValue, Json.Write(overridden));

            active = await factory.Connect(runtime, CancellationToken.None);
            var retainedOverride = await delivery.VerifyEnvironment(runtime, legacy.CredentialConfigurationFingerprint, CancellationToken.None);
            Assert.Equal(overridden.ProcessId, retainedOverride.ProcessId);
            Assert.Equal(overridden.ProcessIncarnation, retainedOverride.ProcessIncarnation);
            Assert.Equal(GitHubProcessEnvironment.TokenOverride, retainedOverride.Status);
            await StopActive();

            active = await factory.Connect(runtime, CancellationToken.None);
            var restarted = await delivery.VerifyEnvironment(runtime, legacy.CredentialConfigurationFingerprint, CancellationToken.None);
            Assert.Equal(GitHubProcessEnvironment.Ready, restarted.Status);
            Assert.NotEqual(legacy.ProcessId, restarted.ProcessId);
            Assert.NotEqual(overridden.ProcessId, restarted.ProcessId);
            Assert.Equal("personal-login", (await Docker("agent", "cat", personal)).Trim());

            await Docker("agent", "sh", "-c", "> " + BootstrapScript.Quote(BootstrapScript.ManagedGitHubConfigDirectory(runtime) + "/hosts.yml"));
            var changed = await delivery.VerifyEnvironment(runtime, legacy.CredentialConfigurationFingerprint, CancellationToken.None);
            Assert.Equal(GitHubProcessEnvironment.CredentialMismatch, changed.Status);
            Assert.Equal("Blocked", changed.State);
            Assert.DoesNotContain(legacy.CredentialConfigurationFingerprint, changed.Detail);
            await Assert.ThrowsAsync<ControlException>(() => delivery.Deliver(runtime,
                new("must-not-overwrite-changed-configuration", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));
        }
        finally
        {
            try { await StopActive(); } catch (Exception) { }
            await Docker("root", "sh", "-c", "rm -rf " + BootstrapScript.Quote(runtime.StateDirectory) +
                " /home/agent/.config/gh /usr/local/bin/gh");
        }

        async Task StopActive()
        {
            if (active is null) return;
            await active.StopOwnedServer(CancellationToken.None);
            await active.DisposeAsync();
            active = null;
        }
    }

    private static async Task<string> Docker(string user, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "-u", user, "hvo-agentcontrol-fixture-a" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private static async Task DockerWrite(string path, string content)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "-i", "-u", "agent", "hvo-agentcontrol-fixture-a", "sh", "-c", "umask 077; cat > \"$1\"", "--", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(content); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error + await output);
    }
}
