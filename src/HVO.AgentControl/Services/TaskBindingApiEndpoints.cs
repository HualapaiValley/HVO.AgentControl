using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Services;

public static class TaskBindingApiEndpoints
{
    public static void MapTaskBindingApi(this RouteGroupBuilder group)
    {
        group.MapGet("/worker-slots", (long? after, int? take, bool? includeArchived, ControlStore store) =>
            store.WorkerSlots(after ?? 0, take ?? 50, includeArchived ?? false));
        group.MapGet("/worker-slots/{id}", (string id, ControlStore store) => store.WorkerSlot(id));
        group.MapPost("/worker-slots", (CreateWorkerSlotInput input, ControlStore store) => store.CreateWorkerSlot(input));
        group.MapPost("/worker-slots/{id}/archive", (string id, ArchiveInventoryInput input, ControlStore store) => store.ArchiveWorkerSlot(id, input));
        group.MapGet("/task-bindings", (long? after, int? take, bool? includeReleased, ControlStore store) =>
            store.TaskBindings(after ?? 0, take ?? 50, includeReleased ?? false));
        group.MapGet("/task-bindings/{id}", (string id, ControlStore store) => store.TaskBinding(id));
        group.MapPost("/task-bindings", (CreateTaskBindingInput input, ControlStore store) => store.CreateTaskBinding(input));
        group.MapPost("/task-bindings/{id}/release", (string id, ReleaseTaskBindingInput input, ControlStore store) => store.ReleaseTaskBinding(id, input));
    }
}
