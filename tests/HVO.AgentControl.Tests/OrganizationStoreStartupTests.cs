using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class OrganizationStoreStartupTests
{
    [Fact]
    public async Task WaitReturnsTheStoreWhenItAppearsAfterHostedServiceStartup()
    {
        OrganizationStore? available = null;
        using var temp = new TempDirectory();
        using var store = new OrganizationStore(Path.Combine(temp.Path, OrganizationStore.DatabaseFileName));
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        var waiting = OrganizationStoreStartup.WaitAsync(
            () => Volatile.Read(ref available),
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10));
        await Task.Delay(40);
        Volatile.Write(ref available, store);

        Assert.Same(store, await waiting);
    }

    [Fact]
    public async Task WaitIsBoundedWhenTheStoreNeverAppears()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await OrganizationStoreStartup.WaitAsync(
            static () => null,
            CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(75),
            pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Null(result);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitHonorsHostShutdownCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationStoreStartup.WaitAsync(
            static () => null,
            cancellation.Token,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task WaitTreatsTransientStoreFaultAsNotReady()
    {
        var calls = 0;
        using var temp = new TempDirectory();
        using var store = new OrganizationStore(Path.Combine(temp.Path, OrganizationStore.DatabaseFileName));
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        var result = await OrganizationStoreStartup.WaitAsync(
            () => Interlocked.Increment(ref calls) < 3
                ? throw new OrganizationStoreException("opening")
                : store,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Same(store, result);
        Assert.True(calls >= 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WaitRejectsNonPositivePollingIntervals(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OrganizationStoreStartup.WaitAsync(
            static () => null,
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(milliseconds)));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-store-startup-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
