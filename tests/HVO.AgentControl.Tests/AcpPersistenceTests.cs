using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpPersistenceTests
{
    [Fact]
    public void SaveThenLoadRoundTripsIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("acp-state-");
        var path = Path.Combine(directory.FullName, "runtime.json");

        var state = RuntimeStateStore.CreateNew("AgentControl Development", () => "org-test");
        state.SessionId = "ses_persisted";
        state.SessionTitle = "AgentControl Development AgentControl";
        state.TmuxOwnerToken = "owner-token";

        RuntimeStateStore.Save(path, state);
        var loaded = RuntimeStateStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("org-test", loaded!.OrganizationId);
        Assert.Equal("AgentControl Development", loaded.OrganizationName);
        Assert.Equal("ses_persisted", loaded.SessionId);
        Assert.Equal("owner-token", loaded.TmuxOwnerToken);
    }

    [Fact]
    public void LoadReturnsNullWhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "acp-missing-" + Guid.NewGuid().ToString("n"), "runtime.json");
        Assert.Null(RuntimeStateStore.Load(path));
    }

    [Fact]
    public void LoadThrowsOnMalformedJsonInsteadOfResetting()
    {
        var directory = Directory.CreateTempSubdirectory("acp-state-bad-");
        var path = Path.Combine(directory.FullName, "runtime.json");
        File.WriteAllText(path, "{ not json");

        var exception = Assert.Throws<RuntimeStateException>(() => RuntimeStateStore.Load(path));
        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadThrowsWhenOrganizationIdMissing()
    {
        var directory = Directory.CreateTempSubdirectory("acp-state-empty-");
        var path = Path.Combine(directory.FullName, "runtime.json");
        File.WriteAllText(path, """{"organizationName":"x"}""");

        Assert.Throws<RuntimeStateException>(() => RuntimeStateStore.Load(path));
    }

    [Fact]
    public void SaveRestrictsStateFilePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("acp-state-mode-");
        var path = Path.Combine(directory.FullName, "runtime.json");
        RuntimeStateStore.Save(path, RuntimeStateStore.CreateNew("Org", () => "org-mode"));

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead, mode & UnixFileMode.UserRead);
        Assert.Equal(UnixFileMode.UserWrite, mode & UnixFileMode.UserWrite);
        Assert.Equal(
            UnixFileMode.None,
            mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    [Fact]
    public void NewOrganizationIdsAreStableLookingAndUnique()
    {
        var first = RuntimeStateStore.NewOrganizationId();
        var second = RuntimeStateStore.NewOrganizationId();

        Assert.StartsWith("org-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(20, first.Length);
    }
}
