using System.Collections;
using System.Reflection;
using HVO.AgentControl.Components.Pages;
using HVO.AgentControl.Core;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationStatusTests
{
    [Theory]
    [InlineData("permission", Delivery.Running, "Running", false, false, "Waiting on permission", false)]
    [InlineData("question", Delivery.Running, "Running", false, false, "Waiting on a question", false)]
    [InlineData(null, Delivery.Unknown, "Unknown", true, false, "Uncertain", false)]
    [InlineData(null, Delivery.Queued, "None", true, false, "Queued", false)]
    [InlineData(null, Delivery.Finished, "NeedsReview", false, true, "Idle", true)]
    [InlineData(null, Delivery.Finished, "Cancelled", false, false, "Cancelled", false)]
    public void CurrentBlockersAndOutcomesDetermineAvailability(string? request, string delivery,
        string outcome, bool ownerOperation, bool oldFailure, string expectedLabel, bool available)
    {
        var worker = new WorkerRecord
        {
            Id = "worker",
            RuntimeId = "runtime",
            Name = "Worker",
            Activity = "Idle",
            Stale = false,
            LastObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Outcome = outcome
        };
        var run = new CoordinationRun { Id = "run", WorkerIdsJson = Json.Write(new[] { worker.Id }) };
        List<CommandRecord> commands = [];
        if (oldFailure) commands.Add(new()
        {
            Id = "old",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Failed,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        commands.Add(new()
        {
            Id = "current",
            WorkerId = worker.Id,
            State = delivery,
            Kind = ownerOperation && delivery == Delivery.Queued ? "Abort" : "Prompt",
            Origin = ownerOperation ? "owner" : "coordinator:run",
            CreatedAt = 2,
            UpdatedAt = 2
        });
        List<PendingRequest> requests = request is null ? [] :
            [new() { WorkerId = worker.Id, Kind = request, State = "Pending" }];
        var page = new Coordination();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ControlPage).GetField("snapshot", flags)!.SetValue(page,
            new ControlSnapshot(1, [], [worker], commands, requests));
        var statuses = (IEnumerable)typeof(Coordination).GetMethod("ParticipantStatuses", flags)!.Invoke(page, [run])!;
        var status = Assert.Single(statuses.Cast<object>());
        Assert.Equal(expectedLabel, status.GetType().GetProperty("StateLabel")!.GetValue(status));
        var aggregate = (string)typeof(Coordination).GetMethod("Aggregate", flags)!.Invoke(page, [run])!;
        Assert.Contains(available ? "Available 1" : "Available 0", aggregate);
        if (oldFailure) Assert.Null(status.GetType().GetProperty("Outcome")!.GetValue(status));
    }
}
