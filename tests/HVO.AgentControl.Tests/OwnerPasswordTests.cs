using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Owner password startup validation. Any configured password file is validated
/// regardless of whether the runtime is enabled; the runtime-enabled mode also
/// requires a file. Failures are sanitized, never echo the file contents, and
/// occur before the host is built so no OpenCode process or provider can start.
/// </summary>
public sealed class OwnerPasswordTests
{
    private const string ExactMinimumPassword = "012345678901234567890123"; // exactly 24 characters

    [Fact]
    public void MissingFileWithDisabledRuntimeIsAllowedForDevelopment()
    {
        Assert.Null(Program.ResolveOwnerPassword(null, controlEnabled: false));
        Assert.Null(Program.ResolveOwnerPassword(string.Empty, controlEnabled: false));
        Assert.Null(Program.ResolveOwnerPassword("   ", controlEnabled: false));
    }

    [Fact]
    public void MissingFileWithEnabledRuntimeFailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ResolveOwnerPassword(null, controlEnabled: true));

        Assert.Contains("Control:OwnerPasswordFile", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Program.MinimumOwnerPasswordLength.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("too-short")]
    [InlineData("01234567890123456789012")] // 23 characters
    [InlineData("\n 01234567890123456789012 \n")] // 23 characters after trim
    public void ConfiguredBlankOrShortPasswordFailsClosedForBothModes(string contents)
    {
        // A configured credential is always expected to be usable, so an
        // empty/short file is rejected even when the runtime is disabled.
        using var file = new TempFile(contents);

        Assert.Throws<InvalidOperationException>(
            () => Program.ResolveOwnerPassword(file.Path, controlEnabled: false));
        Assert.Throws<InvalidOperationException>(
            () => Program.ResolveOwnerPassword(file.Path, controlEnabled: true));
    }

    [Fact]
    public void ConfiguredPasswordIsTrimmedBeforeTheMinimumIsApplied()
    {
        using var file = new TempFile($"\n  {ExactMinimumPassword}\t\n");

        var password = Program.ResolveOwnerPassword(file.Path, controlEnabled: false);

        Assert.Equal(ExactMinimumPassword, password);
        Assert.Equal(Program.MinimumOwnerPasswordLength, password!.Length);
    }

    [Fact]
    public void ConfiguredLongPasswordIsAcceptedWhenRuntimeIsEnabled()
    {
        using var file = new TempFile("  a-fairly-long-owner-password-value  \n");

        var password = Program.ResolveOwnerPassword(file.Path, controlEnabled: true);

        Assert.Equal("a-fairly-long-owner-password-value", password);
    }

    [Fact]
    public void MissingConfiguredPathFailsClosedWithoutLeakingThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"hvo-missing-{Guid.NewGuid():N}", "owner-password");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ResolveOwnerPassword(missing, controlEnabled: false));

        Assert.Contains("could not be read", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missing, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableConfiguredPathFailsClosed()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-owner-dir-");
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => Program.ResolveOwnerPassword(directory.FullName, controlEnabled: false));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShortConfiguredSecretIsNeverIncludedInTheFailureMessage()
    {
        const string secret = "leaky-short-secret-val"; // recognizable and below the minimum
        using var file = new TempFile(secret);

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ResolveOwnerPassword(file.Path, controlEnabled: false));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidConfiguredPasswordFailsHostStartupBeforeTheRuntime(bool controlEnabled)
    {
        // The validation runs before builder.Build(), so the host never starts
        // and no OpenCode process or model provider is launched.
        using var file = new TempFile("leaky-short-secret-val");
        using var factory = new InvalidOwnerPasswordFactory(file.Path, controlEnabled);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Control:OwnerPasswordFile", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("leaky-short-secret-val", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-owner-{Guid.NewGuid():N}.txt");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Disabled-by-default or enabled host configured with a blank/short owner
/// password file. Startup validation must reject it before the runtime starts.
/// </summary>
public sealed class InvalidOwnerPasswordFactory : WebApplicationFactory<Program>
{
    private readonly string _passwordPath;
    private readonly bool _controlEnabled;

    public InvalidOwnerPasswordFactory(string passwordPath, bool controlEnabled)
    {
        _passwordPath = passwordPath;
        _controlEnabled = controlEnabled;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", _controlEnabled ? "true" : "false");
        builder.UseSetting("Control:OwnerPasswordFile", _passwordPath);
    }
}
