using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RequestReplyTests
{
    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task VanishedRequestCancelsUnsentReplyAndPreservesAuditAcrossRestart(string kind)
    {
        string data, secrets, commandId, requestId;
        ReplyInput input;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var request = new PendingRequest { WorkerId = worker.Id, Kind = kind, NativeId = "request-absent" };
            requestId = request.Id;
            await app.Store.Write(db => { db.Requests.Add(request); return Task.FromResult(true); });
            input = new(Guid.NewGuid().ToString(), requestId, kind == "permission" ? "always" : null, null, Reject: kind == "question");
            commandId = (await app.Store.Reply(input)).Id;
            await app.Store.Write(async db => { (await db.Commands.FindAsync(commandId))!.State = Delivery.Dispatching; return true; });
            Assert.True(await app.Store.RecordUnavailableReply(commandId));
            Assert.False(await app.Store.RecordUnavailableReply(commandId));
            var command = await app.Store.Reply(input); // Idempotent retry returns the original non-delivery receipt.
            Assert.Equal(Delivery.Cancelled, command.State);
            Assert.Contains("reply was not sent", command.Detail);
            Assert.Contains("does not confirm approval", command.Detail);
            Assert.Null(command.AcceptedAt);
            Assert.Equal("NoLongerPending", await app.Store.Read(async db => (await db.Requests.FindAsync(requestId))!.State));
            var error = await Assert.ThrowsAsync<ControlException>(() => app.Store.Reply(input with { Id = Guid.NewGuid().ToString() }));
            Assert.Contains("No new reply was sent", error.Message);
            Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "ReplyNotSent" && x.CommandId == commandId)));
        }
        await using var restarted = new TestApp(data, secrets);
        await restarted.Store.Recover();
        Assert.Equal(Delivery.Cancelled, (await restarted.Store.Reply(input)).State);
        Assert.Equal("NoLongerPending", await restarted.Store.Read(async db => (await db.Requests.FindAsync(requestId))!.State));
        Assert.False(await restarted.Store.RecordUnavailableReply(commandId));
    }

    [Theory]
    [InlineData(Delivery.Unknown)]
    [InlineData(Delivery.Accepted)]
    [InlineData(Delivery.Finished)]
    [InlineData(Delivery.Queued)]
    public async Task AbsenceCannotRewritePossibleDeliveryOrSkipPreflight(string state)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var request = new PendingRequest { WorkerId = worker.Id, Kind = "permission", NativeId = "uncertain-request" };
        await app.Store.Write(db => { db.Requests.Add(request); return Task.FromResult(true); });
        var command = await app.Store.Reply(new(Guid.NewGuid().ToString(), request.Id, "once", null));
        await app.Store.Write(async db =>
        {
            (await db.Commands.FindAsync(command.Id))!.State = state;
            (await db.Requests.FindAsync(request.Id))!.State = "ReplyUnknown";
            return true;
        });
        Assert.False(await app.Store.RecordUnavailableReply(command.Id));
        Assert.Equal(state, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.State));
        Assert.Equal("ReplyUnknown", await app.Store.Read(async db => (await db.Requests.FindAsync(request.Id))!.State));
        Assert.Equal(0, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "ReplyNotSent")));
    }
}
