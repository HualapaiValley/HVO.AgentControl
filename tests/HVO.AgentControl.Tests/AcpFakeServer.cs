using System.Diagnostics;
using System.Text;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Helper that launches a deterministic fake ACP server (Python, no model
/// calls) as a real child process for transport tests.
/// </summary>
/// <remarks>
/// The script body lives in the checked-in, never-rewritten
/// <c>Fixtures/fake_acp.py</c> canonical fixture. Runtime test code must not
/// materialize an executable by writing it: on Linux a concurrent fork can
/// duplicate the writable descriptor of a freshly written inode and keep it
/// open until that child's <c>execve</c>, yielding ETXTBSY after the writing parent
/// closes (issue #232). Instead, <see cref="CreateExecutable"/> writes only a
/// non-executable scenario sidecar next to a unique symlink that points at the
/// canonical executable, so the exec'd inode is stable for the whole run.
/// </remarks>
internal static class AcpFakeServer
{
    public const string DefaultSessionId = "ses_fake_0001";

    public const string ScenarioSidecarFileName = "scenario";

    private const string DefaultScenario = "happy";

    private static readonly string CanonicalPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_acp.py");

    /// <summary>
    /// Absolute path to the checked-in canonical fixture copied to the test
    /// output directory. The file is never written at runtime.
    /// </summary>
    public static string PythonScriptPath => CanonicalPath;

    /// <summary>
    /// Creates a unique, executable path for the requested scenario: a symlink
    /// to the canonical fixture plus a non-executable scenario sidecar. The
    /// fixture reads the sidecar relative to the invoked (unresolved) path.
    /// </summary>
    public static string CreateExecutable(string? scenario = null)
    {
        if (!File.Exists(CanonicalPath))
        {
            throw new FileNotFoundException(
                $"Canonical fake ACP fixture was not copied to '{CanonicalPath}'.",
                CanonicalPath);
        }

        var directory = Directory.CreateTempSubdirectory("acp-fake-");
        var executable = Path.Combine(directory.FullName, "fake_acp.py");
        var sidecar = Path.Combine(directory.FullName, ScenarioSidecarFileName);
        var resolvedScenario = string.IsNullOrEmpty(scenario) ? DefaultScenario : scenario;

        // The sidecar is deliberately non-executable: only the symlink target is
        // ever passed to execve.
        File.WriteAllText(sidecar, resolvedScenario, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.CreateSymbolicLink(executable, CanonicalPath);
        return executable;
    }

    public static Process Start(string? scenario, string home, int? maxFrameBytes = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(PythonScriptPath);
        if (!string.IsNullOrEmpty(scenario))
        {
            startInfo.ArgumentList.Add(scenario);
        }

        startInfo.Environment["HOME"] = home;

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the fake ACP process.");
        }

        return process;
    }
}
