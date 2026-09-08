using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Services;

public sealed partial class RuntimeSupervisor
{
    private static async Task<JsonElement?> FindControlSession(OpenCodeClient api, ControlSessionBinding binding, CancellationToken token)
    {
        var sessions = await api.Get(OpenCodeClient.Scope("/session?limit=1000", ControlStore.ControlDirectory), token, 2_000_000);
        var matches = sessions.EnumerateArray().Where(x => x.GetProperty("title").GetString() == binding.Title &&
            x.GetProperty("directory").GetString() == ControlStore.ControlDirectory).ToArray();
        if (matches.Length > 1) throw new ControlException("Multiple native sessions match this control scope. Inspect them before rebinding; no additional session was created.");
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task ReconcileControlCreation(RuntimeRecord runtime, IRuntimeTransport transport, CancellationToken token)
    {
        if (runtime.ConnectionKind != RuntimeConnections.ControlHttp) return;
        var pending = await store.Read(db => db.ControlSessions.AsNoTracking().Where(x => x.ControlServiceId == runtime.Id && x.State != "Ready").ToListAsync(token));
        foreach (var binding in pending)
        {
            var command = await store.Read(db => db.Commands.AsNoTracking().SingleAsync(x => x.Id == binding.CreationCommandId, token));
            if (command.State == Delivery.Unknown || command.State == Delivery.Cancelled && command.Attempts > 0)
            {
                if (await FindControlSession(transport.Api, binding, token) is { } native)
                    await store.BindControlSession(command.Id, native, await transport.Api.Models(ControlStore.ControlDirectory, token));
                // A missing result of an uncertain POST is not proof it was never delivered. Never replay it.
            }
            if (command.State is Delivery.Unknown or Delivery.Failed or Delivery.Cancelled && (binding.State != command.State || binding.Detail != command.Detail))
                await store.Write(async db =>
                {
                    var record = (await db.ControlSessions.FindAsync(binding.Id))!;
                    if (record.State == "Ready") return false;
                    record.State = command.State; record.Detail = command.Detail; record.Revision++;
                    return true;
                });
        }
    }
}
