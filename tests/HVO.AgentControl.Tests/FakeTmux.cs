using System.Text.Json;
using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.Tests;

/// <summary>One recorded invocation of the fake tmux binary.</summary>
internal sealed record TmuxCall(string[] Args, Dictionary<string, string> Env);

/// <summary>
/// Scripted tmux stand-in shared by every test that needs an owned, live attach
/// pane without a real tmux server or a real OpenCode attach client.
/// </summary>
/// <remarks>
/// <para>
/// The binary itself is the checked-in, build-copied canonical fixture
/// <c>Fixtures/fake-tmux.py</c>; each instance only creates a private temporary
/// directory and a <em>symlink</em> to it. Test code must never materialize an
/// executable by writing one: <c>File.WriteAllText</c> holds a writable
/// descriptor, a concurrent <c>Process.Start</c> on another thread forks and
/// inherits it, and the <c>execve</c> of that inode then fails with ETXTBSY
/// ("Text file busy") - measured at roughly 2% of starts under a parallel
/// process load (#232, #233). The symlink keeps the exec'd inode stable for the
/// whole run while the fixture derives its state directory from the invoked
/// (unresolved) path, so every instance stays isolated.
/// </para>
/// <para>
/// <see cref="TestFixtureHygieneTests"/> keeps that rule structural rather than
/// a convention: no test source may write an executable at runtime.
/// </para>
/// </remarks>
internal sealed class FakeTmux : IDisposable
{
    private static readonly string CanonicalFakeTmux =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-tmux.py");

    private readonly string _directory = Directory.CreateTempSubdirectory("tmux-fake-").FullName;
    private readonly string _executable;

    public string OthersPath => Path.Combine(_directory, "others.json");

    public string StatePath => Path.Combine(_directory, "state.json");

    public TmuxAttachRequest Request { get; }

    public FakeTmux()
    {
        if (!File.Exists(CanonicalFakeTmux))
        {
            throw new FileNotFoundException(
                $"Canonical fake tmux fixture was not copied to '{CanonicalFakeTmux}'.",
                CanonicalFakeTmux);
        }

        // A space in the name also proves no argument is shell-parsed.
        _executable = Path.Combine(_directory, "fake tmux");
        File.CreateSymbolicLink(_executable, CanonicalFakeTmux);

        Request = new(
            "http://127.0.0.1:12345",
            Path.Combine(_directory, "work space"),
            Path.Combine(_directory, "home"),
            "ses_test",
            "opencode",
            "ephemeral",
            "owner",
            true,
            "/pinned path/opencode");
    }

    /// <summary>
    /// A launcher bound to this fake. <paramref name="sessionName"/> only has to
    /// match what the fixture's own state records, so callers that inject the
    /// launcher into a host can keep their own name.
    /// </summary>
    public TmuxAttachLauncher Launcher(TimeProvider? clock = null, string sessionName = "test") =>
        new(sessionName, _executable, clock);

    public void State(string owner, bool dead, string target = "dead")
    {
        var panes = new Dictionary<string, int> { ["%99"] = 0 };
        if (target != "missing")
        {
            panes["%1"] = dead ? 1 : 0;
        }

        File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            id = "$0",
            name = "test",
            owner,
            panes,
            identity = target == "unknown" ? "" : owner + ":%1",
        }));
    }

    /// <summary>
    /// Arms a deterministic tmux server restart: after the next
    /// <c>list-panes</c> and the owner recheck that follows it, the current
    /// server is replaced by an unrelated session reusing id <c>$0</c> under
    /// <paramref name="name"/>.
    /// </summary>
    public void ArmServerRestartAfterOwnerRecheck(string name) =>
        File.WriteAllText(Path.Combine(_directory, "arm"), name);

    /// <summary>Adds an unowned session whose name merely shares our prefix.</summary>
    public void AddOverlappingSession(string name) => File.WriteAllText(
        OthersPath,
        JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "$7",
                name,
                owner = "foreign",
                panes = new Dictionary<string, int> { ["%77"] = 0 },
                identity = "foreign:%77",
            },
        }));

    /// <summary>Makes one tmux subcommand fail; null clears the injection.</summary>
    public void Fail(string? command) =>
        File.WriteAllText(Path.Combine(_directory, "failure"), command ?? "");

    public TmuxCall[] Calls()
    {
        var path = Path.Combine(_directory, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<TmuxCall>(line)!).ToArray()
            : [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
