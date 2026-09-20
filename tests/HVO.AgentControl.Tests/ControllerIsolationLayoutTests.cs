using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The controller/agent split as it is expressed in configuration and on disk:
/// private state separate from the agent tree, orientation in a host-owned
/// location, and a reversible adoption of the pre-isolation runtime file.
/// </summary>
public sealed class ControllerIsolationLayoutTests
{
    [Fact]
    public void IsolationOptionsDefaultToTheSingleIdentityLayout()
    {
        var options = new ControlOptions();

        // Host development and the disabled CI runtime must keep working with no
        // launcher and no separate private store.
        Assert.Equal(string.Empty, options.PrivateDataDirectory);
        Assert.Equal(string.Empty, options.InstructionsDirectory);
        Assert.Equal(string.Empty, options.AgentLauncher);
        Assert.Equal(options.DataDirectory, options.ResolvePrivateDataDirectory());
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ConfiguredPrivateDirectoryOverridesTheAgentDataRoot()
    {
        var options = new ControlOptions
        {
            DataDirectory = "/data",
            PrivateDataDirectory = "/control-data",
        };

        Assert.Equal("/control-data", options.ResolvePrivateDataDirectory());
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("relative/private", "", "")]
    [InlineData("", "relative/config", "")]
    [InlineData("", "", "agentcontrol-launch")]
    public void RelativeIsolationPathsAreRejected(string privateData, string instructions, string launcher)
    {
        var options = new ControlOptions
        {
            PrivateDataDirectory = privateData,
            InstructionsDirectory = instructions,
            AgentLauncher = launcher,
        };

        // A relative value would resolve against the controller's working
        // directory and quietly place private state outside the private volume.
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void DatabasePathIsFixedToControlDbInThePrivateDirectory()
    {
        var isolated = new ControlOptions
        {
            DataDirectory = "/data",
            PrivateDataDirectory = "/control-data",
        };
        Assert.Equal("/control-data/control.db", isolated.ResolveDatabasePath());

        // Single-identity host development keeps the database beside the runtime state.
        var development = new ControlOptions { DataDirectory = "/tmp/hvo-dev" };
        Assert.Equal(Path.Combine("/tmp/hvo-dev", "control.db"), development.ResolveDatabasePath());
        Assert.Empty(development.Validate());

        // There is no public override: the path is always derived from the
        // controller-private directory, so no deployment can select a different
        // authoritative store than the rollback tooling and layout preparation
        // expect.
        Assert.Null(typeof(ControlOptions).GetProperty("DatabasePath"));
        Assert.Equal(OrganizationStore.DatabaseFileName, Path.GetFileName(isolated.ResolveDatabasePath()));
    }

    [Fact]
    public void EmptyAdoptionReferenceIsRejectedAndComposeSetsNoDatabaseOverride()
    {
        var missingReference = new ControlOptions { AdoptionAuthorizationReference = "  " };
        Assert.NotEmpty(missingReference.Validate());

        // The Compose environment sets the controller-private directory (which
        // fixes the database location) and never sets a path override.
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "compose.yaml"));
        Assert.Contains("Control__PrivateDataDirectory: /control-data", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Control__DatabasePath", compose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The initial Operations/IT seed is the owner-approved #211 adoption, and
    /// the audit must carry that exact reference. It is not a model assertion and
    /// it is not re-derived from the environment.
    /// </summary>
    [Fact]
    public void AdoptionAuthorizationReferenceIsTheExactOwnerApprovedIssue211()
    {
        var options = new ControlOptions();

        Assert.Equal("owner-approved:issue-211", options.AdoptionAuthorizationReference);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ComposeConfiguresTheIsolatedLayoutAndNotNoNewPrivileges()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "compose.yaml"));

        Assert.Contains("Control__PrivateDataDirectory: /control-data", compose, StringComparison.Ordinal);
        Assert.Contains("Control__InstructionsDirectory: /agent-config", compose, StringComparison.Ordinal);
        Assert.Contains(
            "Control__AgentLauncher: /usr/local/bin/agentcontrol-launch",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("control-private:/control-data", compose, StringComparison.Ordinal);

        // The control service's setuid launcher cannot elevate under
        // no-new-privileges. A separate optional worker service may and should
        // use that hardening because its root PID1 performs direct fixed-UID
        // lifecycle operations and has no setuid artifact.
        var controlSection = compose[..compose.IndexOf("  docker-helper:", StringComparison.Ordinal)];
        Assert.DoesNotContain("no-new-privileges:true", controlSection, StringComparison.Ordinal);
        Assert.Contains("cap_drop", controlSection, StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackRunbookSelectsTheRetainedImageWithoutBuilding()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

        Assert.Contains("agentcontrol-v2-control:rollback-pre-isolation", readme, StringComparison.Ordinal);
        Assert.Contains("image inspect agentcontrol-v2-control:latest", readme, StringComparison.Ordinal);
        Assert.Contains("compose up -d --no-build", readme, StringComparison.Ordinal);
        Assert.Contains("Verify the running container's image ID", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateStateDirectoryIsCreatedWithoutGroupOrOtherAccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var host = CreateHost(new ControlOptions
        {
            Enabled = false,
            DataDirectory = root.Combine("data"),
            PrivateDataDirectory = root.Combine("private"),
            InstructionsDirectory = root.Combine("agent-config"),
        });

        Invoke(host, "PrepareDirectories");

        var mode = File.GetUnixFileMode(root.Combine("private"));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode);
    }

    [Fact]
    public void OrientationDirectoryIsHostOwnedAndAgentReadable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var host = CreateHost(new ControlOptions
        {
            Enabled = false,
            DataDirectory = root.Combine("data"),
            PrivateDataDirectory = root.Combine("private"),
            InstructionsDirectory = root.Combine("agent-config"),
        });

        Invoke(host, "PrepareDirectories");

        var mode = File.GetUnixFileMode(root.Combine("agent-config"));

        // Readable and traversable by the agent, writable only by the owner, so
        // the agent cannot replace or unlink the orientation file.
        Assert.True(mode.HasFlag(UnixFileMode.OtherRead));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
    }

    [Fact]
    public void LegacyRuntimeStateIsAdoptedIntoThePrivateStoreWithoutBeingDestroyed()
    {
        using var root = new TempRoot();
        var data = root.Combine("data");
        var privateData = root.Combine("private");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(privateData);

        var legacyPath = Path.Combine(data, "runtime.json");
        var legacy = RuntimeStateStore.CreateNew("Contoso", () => "org-legacy");
        legacy.SessionId = "ses_existing";
        legacy.TmuxOwnerToken = "legacy-owner-token";
        RuntimeStateStore.Save(legacyPath, legacy);
        var legacyBytes = File.ReadAllBytes(legacyPath);

        var host = CreateHost(new ControlOptions
        {
            Enabled = false,
            DataDirectory = data,
            PrivateDataDirectory = privateData,
        });

        var evidence = (PersistedRuntimeEvidence)Invoke(host, "LoadAdoptionEvidence")!;
        var adopted = evidence.State;

        // Existing organization/session identity survives the move...
        Assert.Equal("org-legacy", adopted.OrganizationId);
        Assert.Equal("ses_existing", adopted.SessionId);
        Assert.Equal("legacy-owner-token", adopted.TmuxOwnerToken);
        Assert.Equal(legacyBytes, evidence.Bytes);
        Assert.Equal(Path.GetFullPath(legacyPath), evidence.Path);

        // ...and the legacy file is left untouched as the rollback copy.
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void AnExistingPrivateStateIsNeverOverwrittenByTheLegacyFile()
    {
        using var root = new TempRoot();
        var data = root.Combine("data");
        var privateData = root.Combine("private");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(privateData);

        var legacy = RuntimeStateStore.CreateNew("Contoso", () => "org-legacy");
        legacy.SessionId = "ses_stale";
        RuntimeStateStore.Save(Path.Combine(data, "runtime.json"), legacy);

        var current = RuntimeStateStore.CreateNew("Contoso", () => "org-current");
        current.SessionId = "ses_current";
        RuntimeStateStore.Save(Path.Combine(privateData, "runtime.json"), current);

        var host = CreateHost(new ControlOptions
        {
            Enabled = false,
            DataDirectory = data,
            PrivateDataDirectory = privateData,
        });

        var loaded = ((PersistedRuntimeEvidence)Invoke(host, "LoadAdoptionEvidence")!).State;

        Assert.Equal("org-current", loaded.OrganizationId);
        Assert.Equal("ses_current", loaded.SessionId);
    }

    [Fact]
    public void IsolatedRuntimeRefusesToRecreateAMissingAgentTree()
    {
        using var root = new TempRoot();
        var host = CreateHost(new ControlOptions
        {
            Enabled = false,
            DataDirectory = root.Combine("data"),
            PrivateDataDirectory = root.Combine("private"),
            AgentLauncher = "/usr/local/bin/agentcontrol-launch",
        });

        // With a launcher configured the agent tree belongs to another UID: a
        // controller-created replacement would have the wrong owner and silently
        // break isolation, so this must fault instead.
        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => Invoke(host, "PrepareDirectories"));
        Assert.IsType<AcpProtocolException>(exception.InnerException);
    }

    /// <summary>
    /// Once the authoritative database exists, runtime.json is evidence only.
    /// A JSON-only divergence that used to block the isolated start must not
    /// fault the database-era image, while the refusal is still recorded (and
    /// enforced) before any database exists.
    /// </summary>
    [Fact]
    public void PrepareLayoutTreatsJsonAsEvidenceWhenTheDatabaseExists()
    {
        var layout = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "container", "prepare-layout.py"));

        Assert.Contains("database_authoritative = exists(control_fd, \"control.db\")", layout, StringComparison.Ordinal);
        Assert.Contains(
            "legacy_info.st_uid != 0 and private_payload != payload and not database_authoritative:",
            layout,
            StringComparison.Ordinal);

        // The database-era tolerance is labelled with truthful provenance. The
        // JSON divergence is not recorded anywhere, so the log must not claim it
        // was, and the provenance label is what the container test asserts.
        Assert.Contains("provenance=database-era-json-divergence", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("is recorded as evidence rather than blocking", layout, StringComparison.Ordinal);

        // The non-database refusal is still present and still records the marker.
        Assert.Contains("record_unrecorded_divergence(", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedConfigDeniesTheControllerPrivateStore()
    {
        var json = AgentControlOpenCodeConfig.Build(
            "opencode/big-pickle",
            "/agent-config/agentcontrol-instructions.md");

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var permission = document.RootElement.GetProperty("permission");

        Assert.Equal("deny", permission.GetProperty("read").GetProperty("/control-data/**").GetString());
        Assert.Equal("deny", permission.GetProperty("edit").GetProperty("/control-data/**").GetString());
        Assert.Equal("deny", permission.GetProperty("edit").GetProperty("/agent-config/**").GetString());
    }

    private static AcpControlHost CreateHost(ControlOptions options) =>
        new(Options.Create(options), NullLogger<AcpControlHost>.Instance);

    private static object? Invoke(AcpControlHost host, string method) =>
        typeof(AcpControlHost)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(host, null);

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root");
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentcontrol-isolation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
