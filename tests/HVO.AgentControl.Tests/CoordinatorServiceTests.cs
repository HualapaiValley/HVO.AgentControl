using System.Reflection;
using HVO.AgentControl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinatorServiceTests
{
    private static Task Iterate(Func<Task> tick, CancellationToken token = default) =>
        (Task)typeof(CoordinatorService).GetMethod("RunIteration", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [tick, NullLogger.Instance, token])!;

    [Fact]
    public async Task TickAndRecoveryPersistenceFailuresDoNotPreventTheNextIteration()
    {
        var successfulTicks = 0;
        await Iterate(() => throw new AggregateException(new InvalidOperationException("Injected tick failure"),
            new IOException("Injected recovery storage failure")));
        await Iterate(() => { successfulTicks++; return Task.CompletedTask; });
        Assert.Equal(1, successfulTicks);
    }

    [Fact]
    public async Task HostShutdownCancellationIsNotRetried()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Iterate(() => Task.FromCanceled(stop.Token), stop.Token));
    }
}
