using System.Reflection;
using HVO.AgentControl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinatorServiceTests
{
    private static Task Iterate(Func<Task> tick, Func<string, Task> recover, CancellationToken token = default) =>
        (Task)typeof(CoordinatorService).GetMethod("RunIteration", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [tick, recover, NullLogger.Instance, token])!;

    [Fact]
    public async Task TickAndRecoveryPersistenceFailuresDoNotPreventTheNextIteration()
    {
        var recoveryAttempts = 0;
        var successfulTicks = 0;
        await Iterate(() => throw new InvalidOperationException("Injected tick failure"), detail =>
        {
            Assert.Contains("InvalidOperationException", detail);
            recoveryAttempts++;
            throw new IOException("Injected recovery storage failure");
        });
        await Iterate(() => { successfulTicks++; return Task.CompletedTask; }, _ => throw new Exception("Recovery must not run after success"));
        Assert.Equal(1, recoveryAttempts);
        Assert.Equal(1, successfulTicks);
    }

    [Fact]
    public async Task HostShutdownCancellationIsNotRetried()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var attemptedRecovery = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Iterate(
            () => Task.FromCanceled(stop.Token), _ => { attemptedRecovery = true; return Task.CompletedTask; }, stop.Token));
        Assert.False(attemptedRecovery);
    }
}
