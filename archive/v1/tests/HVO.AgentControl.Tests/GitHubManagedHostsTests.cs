using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.GitHub;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class GitHubManagedHostsTests
{
    private const string Actor = "agentcontrol-test[bot]";
    private const string Token = "ghs_DISPOSABLE_FORMAT_FIXTURE_ONLY";

    private sealed class GitHubCliFactAttribute : FactAttribute
    {
        public GitHubCliFactAttribute()
        { if (Environment.GetEnvironmentVariable("HVO_GITHUB_CLI_TESTS") != "1") Skip = "Set HVO_GITHUB_CLI_TESTS=1 with GitHub CLI and POSIX tools installed."; }
    }

    [Fact]
    public void SafeScalarRewritesRetainThePreviouslyDeliveredFingerprint()
    {
        var canonical = Hosts(Token);
        var oldFingerprint = RawHash(canonical);
        foreach (var rewritten in EquivalentForms(canonical))
        {
            Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(rewritten, Actor));
            Assert.Equal(oldFingerprint, GitHubProcessEnvironment.CredentialFingerprint(rewritten));
        }
    }

    [Theory]
    [InlineData('a')]
    [InlineData('é')]
    public void MaximumValidatedInstallationResponseRemainsReadableAfterDelivery(char character)
    {
        var actor = new string(character, 200) + "[bot]";
        var canonical = GitHubCredentialDelivery.HostsYaml(new(new string(character, 8192),
            DateTimeOffset.UtcNow.AddHours(1), actor));
        Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(canonical, actor));
        Assert.Equal(RawHash(canonical), GitHubProcessEnvironment.CredentialFingerprint(canonical));
    }

    [Fact]
    public void ExtraIdentityChangedTokensAndUnsupportedScalarsCannotBecomeManagedCredentials()
    {
        var canonical = Hosts(Token);
        foreach (var invalid in InvalidForms(canonical))
            Assert.False(GitHubCredentialDelivery.IsExclusivelyManagedHosts(invalid, Actor));
        var changed = Hosts("ghs_DIFFERENT_DISPOSABLE_TOKEN");
        Assert.NotEqual(GitHubProcessEnvironment.CredentialFingerprint(canonical),
            GitHubProcessEnvironment.CredentialFingerprint(changed));
    }

    [Fact]
    public void DotSeparatedTokenRewritePreservesOwnershipAndPreviouslyDeliveredFingerprint()
    {
        // Match the observed length/character structure using entirely disposable values.
        var token = "ghs_" + new string('a', 180) + "." + new string('b', 205);
        var canonical = Hosts(token);
        foreach (var rewritten in EquivalentForms(canonical, token))
        {
            Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(rewritten, Actor));
            Assert.Equal(RawHash(canonical), GitHubProcessEnvironment.CredentialFingerprint(rewritten));
        }
        var plain = EquivalentForms(canonical, token).Last();
        foreach (var invalid in new[]
        {
            plain.Replace("            oauth_token: " + token, "            oauth_token: " + token + ".changed", StringComparison.Ordinal),
            plain + "enterprise.example.com:\n    user: personal\n",
            plain.Replace("    users:\n", "    users:\n        personal:\n            oauth_token: personal\n", StringComparison.Ordinal),
            plain.Replace(token, token + " # comment", StringComparison.Ordinal),
            plain.Replace(token, "&alias " + token, StringComparison.Ordinal),
            plain.Replace(token, ".nan", StringComparison.Ordinal),
            plain.Replace(token, "1.5", StringComparison.Ordinal)
        })
        {
            Assert.False(GitHubCredentialDelivery.IsExclusivelyManagedHosts(invalid, Actor));
            Assert.NotEqual(RawHash(canonical), GitHubProcessEnvironment.CredentialFingerprint(invalid));
        }
        Assert.False(GitHubCredentialDelivery.IsExclusivelyManagedHosts(plain, "different-app[bot]"));
        Assert.False(GitHubCredentialDelivery.IsManagedConfigurationReplacementAllowed(true, "another-runtime", "owned-runtime"));
    }

    [GitHubCliFact]
    public async Task InstalledCliUsageAndTwoRenewalsPreserveCanonicalAndRemoteFingerprints()
    {
        var directory = TemporaryDirectory();
        try
        {
            // No network credentials, inherited auth, personal gh directory or global
            // Git configuration are available to these installed CLI invocations.
            for (var renewal = 0; renewal < 3; renewal++)
            {
                var value = renewal == 0 ? Token : Token + "." + renewal + ".DISPOSABLE_SIGNATURE";
                var canonical = Hosts(value);
                if (File.Exists(Path.Combine(directory, "hosts.yml")))
                    Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(
                        await File.ReadAllTextAsync(Path.Combine(directory, "hosts.yml")), Actor));
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), canonical);
                var expected = RawHash(canonical); // compatible with the deployed byte hash
                var token = await Run(directory, "gh", ["auth", "token", "--hostname", "github.com"]);
                Assert.True(token.ExitCode == 0 && token.Output.Trim() == value, "Isolated CLI token read failed.");
                var rewritten = await File.ReadAllTextAsync(Path.Combine(directory, "hosts.yml"));
                Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(rewritten, Actor));
                Assert.Equal(expected, GitHubProcessEnvironment.CredentialFingerprint(rewritten));
                Assert.Equal(expected, await PortableFingerprint(directory));
                var setup = await Run(directory, "gh", ["auth", "setup-git", "--hostname", "github.com"]);
                Assert.True(setup.ExitCode == 0, "Isolated CLI Git setup failed.");
                rewritten = await File.ReadAllTextAsync(Path.Combine(directory, "hosts.yml"));
                Assert.True(GitHubCredentialDelivery.IsExclusivelyManagedHosts(rewritten, Actor));
                Assert.Equal(expected, GitHubProcessEnvironment.CredentialFingerprint(rewritten));
                Assert.Equal(expected, await PortableFingerprint(directory));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [GitHubCliFact]
    public async Task PortableFingerprintMatchesSafeFormsAndRejectsChangedOrAmbiguousConfiguration()
    {
        var directory = TemporaryDirectory();
        var canonical = Hosts(Token);
        var expected = RawHash(canonical);
        try
        {
            foreach (var equivalent in EquivalentForms(canonical))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), equivalent);
                Assert.Equal(expected, await PortableFingerprint(directory));
            }
            var segmentedToken = Token + ".DISPOSABLE_SIGNATURE";
            var segmented = Hosts(segmentedToken);
            foreach (var equivalent in EquivalentForms(segmented, segmentedToken))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), equivalent);
                Assert.Equal(RawHash(segmented), await PortableFingerprint(directory));
            }
            var changedSegment = Hosts(segmentedToken + ".changed");
            await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), changedSegment);
            Assert.NotEqual(RawHash(segmented), await PortableFingerprint(directory));
            foreach (var invalid in InvalidForms(canonical).Append(Hosts("ghs_CHANGED_TOKEN")).Append(""))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), invalid);
                Assert.NotEqual(expected, await PortableFingerprint(directory));
            }
            foreach (var character in new[] { 'a', 'é' })
            {
                var actor = new string(character, 200) + "[bot]";
                var maximum = GitHubCredentialDelivery.HostsYaml(new(new string(character, 8192),
                    DateTimeOffset.UtcNow.AddHours(1), actor));
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), maximum);
                Assert.Equal(RawHash(maximum), await PortableFingerprint(directory));
            }
            // Plain YAML booleans/numbers are not equivalent to quoted token strings.
            foreach (var ambiguous in new[] { "true", "FALSE", "null", "Yes", "123" })
            {
                canonical = Hosts(ambiguous);
                var plain = canonical.Replace('"' + ambiguous + '"', ambiguous, StringComparison.Ordinal);
                Assert.False(GitHubCredentialDelivery.IsExclusivelyManagedHosts(plain, Actor));
                await File.WriteAllTextAsync(Path.Combine(directory, "hosts.yml"), plain);
                Assert.NotEqual(RawHash(canonical), await PortableFingerprint(directory));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static IEnumerable<string> EquivalentForms(string canonical, string token = Token)
    {
        yield return canonical;
        yield return canonical.Replace("    user: \"" + Actor + "\"", "    user: " + Actor, StringComparison.Ordinal)
            .Replace("            oauth_token: \"" + token + "\"", "            oauth_token: " + token, StringComparison.Ordinal);
        yield return canonical.Replace('"' + Actor + '"', Actor, StringComparison.Ordinal)
            .Replace('"' + token + '"', token, StringComparison.Ordinal);
    }

    private static IEnumerable<string> InvalidForms(string canonical)
    {
        yield return canonical + "enterprise.example.com:\n    user: personal\n";
        yield return canonical.Replace("    users:\n", "    users:\n        personal:\n            oauth_token: personal\n", StringComparison.Ordinal);
        yield return canonical.Replace("    user: \"" + Actor + "\"", "    user: personal", StringComparison.Ordinal);
        yield return canonical.Replace("            oauth_token: \"" + Token + "\"", "            oauth_token: different-token", StringComparison.Ordinal);
        yield return canonical.Replace('"' + Token + '"', "'" + Token + "'", StringComparison.Ordinal);
        yield return canonical.Replace('"' + Token + '"', Token + " # comment", StringComparison.Ordinal);
        yield return canonical.Replace('"' + Token + '"', "&alias " + Token, StringComparison.Ordinal);
        yield return canonical.TrimEnd('\n');
        yield return canonical + "\n";
        yield return canonical.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string Hosts(string token) => GitHubCredentialDelivery.HostsYaml(new(token, DateTimeOffset.UtcNow.AddHours(1), Actor));
    private static string RawHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-gh-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> PortableFingerprint(string directory)
    {
        var result = await Run(directory, "sh", ["-s"], ManagedGitHubHosts.FingerprintNormalizationScript + " | sha256sum\n");
        Assert.True(result.ExitCode == 0, "Portable credential fingerprint failed.");
        return result.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static async Task<(int ExitCode, string Output)> Run(string directory, string executable, string[] arguments, string? input = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["GH_CONFIG_DIR"] = directory;
        start.Environment["config"] = directory;
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "isolated.gitconfig");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var proxy in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) start.Environment[proxy] = "http://127.0.0.1:1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        _ = await error; // Never place CLI credential output in failure diagnostics.
        return (process.ExitCode, await output);
    }
}
