using System.Text;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpNotificationChannelTests
{
    [Fact]
    public async Task BoundedChannelDropsOldestObservationUpdates()
    {
        var lines = new StringBuilder();
        for (var i = 1; i <= 5; i++)
        {
            lines.Append("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"n\":").Append(i).Append("}}\n");
        }

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(lines.ToString()));
        await using var session = new AcpRpcSession(
            input,
            static (_, _) => ValueTask.CompletedTask,
            notificationCapacity: 1);

        session.Start();
        await session.Completion;

        var observed = new List<int>();
        await foreach (var notification in session.Notifications.ReadAllAsync())
        {
            observed.Add(notification.GetProperty("n").GetInt32());
        }

        Assert.Equal(new[] { 5 }, observed);
    }

    [Fact]
    public async Task InvalidCapacityFallsBackToSingleSlot()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"n\":1}}\n"));
        await using var session = new AcpRpcSession(
            input,
            static (_, _) => ValueTask.CompletedTask,
            notificationCapacity: 0);

        session.Start();
        await session.Completion;

        var observed = new List<int>();
        await foreach (var notification in session.Notifications.ReadAllAsync())
        {
            observed.Add(notification.GetProperty("n").GetInt32());
        }

        Assert.Equal(new[] { 1 }, observed);
    }
}
