using System.Collections;
using System.Reflection;
using HVO.AgentControl.Components.Pages;
using HVO.AgentControl.Core;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationStatusTests
{
    [Theory]
    [InlineData("permission", Delivery.Running, "Running", false, false, "Waiting on permission", false, false)]
    [InlineData("question", Delivery.Running, "Running", false, false, "Waiting on a question", false, false)]
    [InlineData(null, Delivery.Unknown, "Unknown", true, false, "Uncertain", false, false)]
    [InlineData(null, Delivery.Queued, "None", true, false, "Queued", false, false)]
    [InlineData(null, Delivery.Finished, "NeedsReview", false, true, "Idle", true, false)]
    [InlineData(null, Delivery.Finished, "Cancelled", false, false, "Cancelled", false, false)]
    [InlineData("both", Delivery.Running, "Running", false, false, "Waiting on permission and a question", false, false)]
    [InlineData(null, Delivery.Unknown, "Running", true, false, "Uncertain", false, true)]
    public void CurrentBlockersAndOutcomesDetermineAvailability(string? request, string delivery,
        string outcome, bool ownerOperation, bool oldFailure, string expectedLabel, bool available, bool anotherRunning)
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
        if (anotherRunning) commands.Add(new()
        {
            Id = "another",
            WorkerId = worker.Id,
            State = Delivery.Running,
            Kind = "Prompt",
            Origin = "owner",
            CreatedAt = 3,
            UpdatedAt = 3
        });
        string[] requestKinds = request is null ? [] : request == "both" ? ["question", "permission"] : [request];
        var requests = requestKinds.Select(kind => new PendingRequest { WorkerId = worker.Id, Kind = kind, State = "Pending" }).ToList();
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
