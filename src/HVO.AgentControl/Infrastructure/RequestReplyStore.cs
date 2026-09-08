using HVO.AgentControl.Core;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    // Called only when reply preflight failed before api.Reply could begin. A new request ID
    // may safely answer the still-pending native request; a post-send failure remains unknown.
    public Task<bool> RecordUnsentReplyPreflightFailure(string commandId, string detail) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(commandId);
        if (command is not { Kind: "Reply", State: Delivery.Dispatching }) return false;
        var input = Json.Read<ReplyInput>(command.Payload);
        var request = await db.Requests.FindAsync(input.RequestId);
        command.State = Delivery.Failed;
        var release = request is not null && request.WorkerId == command.WorkerId && request.State == "Pending" && request.ReplyCommandId == command.Id;
        command.Detail = release
            ? "Reply was not sent because read-only native preflight failed. The request remains pending and can be answered with a new reply request."
            : "Reply was not sent because read-only native preflight failed. The request changed before recovery, so its current reply binding was preserved.";
        command.UpdatedAt = Now;
        if (release) request!.ReplyCommandId = null;
        Event(db, "ReplyPreflightFailed", command.RuntimeId, command.WorkerId, command.Id,
            new { requestId = input.RequestId, detail, released = release, observedAt = command.UpdatedAt },
            provenance: "service", nativeId: release ? request!.NativeId : null);
        return true;
    });

    // Called only when reply preflight successfully observed the native request absent,
    // before any reply mutation. Absence after a send cannot establish its outcome.
    public Task<bool> RecordUnavailableReply(string commandId) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(commandId);
        if (command is not { Kind: "Reply", State: Delivery.Dispatching }) return false;
        var input = Json.Read<ReplyInput>(command.Payload);
        var request = await db.Requests.FindAsync(input.RequestId);
        if (request is null || request.WorkerId != command.WorkerId || request.ReplyCommandId != command.Id)
            return false;
        command.State = Delivery.Cancelled;
        command.Detail = "Request no longer pending on the worker; reply was not sent. This does not confirm approval or rejection.";
        command.UpdatedAt = Now;
        request.State = "NoLongerPending";
        Event(db, "ReplyNotSent", command.RuntimeId, command.WorkerId, command.Id,
            new
            {
                requestId = request.Id,
                request.Kind,
                request.NativeId,
                observedAt = command.UpdatedAt,
                reason = "NativeRequestNoLongerPending",
                state = command.State
            }, provenance: "service", nativeId: request.NativeId);
        return true;
    });
}
