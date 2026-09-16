using System.Text.RegularExpressions;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Structural guard for the executable-fixture rule behind #232/#233.
/// </summary>
/// <remarks>
/// <para>
/// Materializing an executable at runtime and then exec'ing it loses a race
/// under parallel test execution. <c>File.WriteAllText</c> holds a writable
/// descriptor; a concurrent <c>Process.Start</c> on another thread forks and
/// inherits it, and that child's <c>execve</c> of the freshly written inode
/// fails with <c>ETXTBSY</c> ("Text file busy") - measured at roughly 2% of
/// starts under load. The failures are rare, unreproducible and land in an
/// unrelated assertion, which is exactly why the rule is enforced here instead
/// of being left to review.
/// </para>
/// <para>
/// Executables therefore live as checked-in, build-copied canonical fixtures in
/// <c>Fixtures/</c>, and tests symlink to them and configure them through
/// non-executable sidecars. This test fails the build when a test source
/// reintroduces the pattern, rather than waiting for a flake to reappear.
/// </para>
/// </remarks>
public sealed class TestFixtureHygieneTests
{
    /// <summary>Test sources may never set an execute bit on a file they wrote.</summary>
    [Fact]
    public void NoTestSourceMakesAFileExecutableAtRuntime()
    {
        var offenders = TestSources()
            .Where(source => Regex.IsMatch(
                File.ReadAllText(source),
                @"SetUnixFileMode\((?:[^;]*?)Execute",
                RegexOptions.Singleline))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These test sources make a file executable at runtime, which races into ETXTBSY "
            + "(#232/#233). Add a canonical fixture under Fixtures/ and symlink to it instead: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Nor may they write a script body: an interpreter shebang in a written
    /// payload is the same defect a moment before the execute bit is set.
    /// </summary>
    [Fact]
    public void NoTestSourceWritesAScriptBody()
    {
        var offenders = TestSources()
            .Where(source => File.ReadAllText(source).Contains("#!/", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These test sources embed a script body to be written at runtime. Move it to a "
            + "checked-in Fixtures/ file and symlink to it: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every canonical fixture must actually reach the output directory, or the
    /// symlink-based helpers fail at run time with a missing-file error that
    /// looks like an environment problem rather than a wiring mistake.
    /// </summary>
    [Fact]
    public void EveryCanonicalFixtureIsCopiedToTheOutputDirectory()
    {
        var source = Path.Combine(
            ControllerIsolationLayoutTests.RepositoryRoot(),
            "tests",
            "HVO.AgentControl.Tests",
            "Fixtures");
        var expected = Directory.GetFiles(source).Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(expected);

        var copied = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        Assert.True(Directory.Exists(copied), $"No fixtures were copied to '{copied}'.");
        var actual = Directory.GetFiles(copied).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(expected, actual);

        if (!OperatingSystem.IsWindows())
        {
            // Only fixtures that are executed as programs need the execute bit. Data
            // fixtures (captured command output replayed by a parser) are read, never
            // run, and marking them executable would be misleading rather than safe.
            foreach (var name in expected.Where(IsExecutableFixture))
            {
                Assert.True(
                    File.GetUnixFileMode(Path.Combine(copied, name!)).HasFlag(UnixFileMode.UserExecute),
                    $"Fixture '{name}' lost its execute bit when it was copied to the output directory.");
            }

            foreach (var name in expected.Where(candidate => !IsExecutableFixture(candidate)))
            {
                Assert.False(
                    File.GetUnixFileMode(Path.Combine(copied, name!)).HasFlag(UnixFileMode.UserExecute),
                    $"Data fixture '{name}' is executable; only fixtures that are run as programs may be.");
            }
        }
    }

    /// <summary>A fixture that is launched as a program rather than read as data.</summary>
    private static bool IsExecutableFixture(string? name) =>
        name is not null && (name.EndsWith(".sh", StringComparison.Ordinal) || name.EndsWith(".py", StringComparison.Ordinal));

    /// <summary>
    /// Every test source except this one, which necessarily names the patterns
    /// it forbids.
    /// </summary>
    private static string[] TestSources()
    {
        var sources = Directory.GetFiles(
            Path.Combine(
                ControllerIsolationLayoutTests.RepositoryRoot(),
                "tests",
                "HVO.AgentControl.Tests"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != nameof(TestFixtureHygieneTests) + ".cs")
            .ToArray();

        // A guard that silently scanned nothing would report a permanent pass.
        Assert.True(sources.Length > 10, $"only {sources.Length} test sources were found to scan");
        return sources;
    }
}
