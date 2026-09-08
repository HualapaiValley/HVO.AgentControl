using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Services;

public static class TaskBindingApiEndpoints
{
    public static void MapTaskBindingApi(this RouteGroupBuilder group)
    {
        group.MapGet("/worker-slots", async (long? after, int? take, bool? includeArchived, ControlStore store) =>
            await Execute(() => store.WorkerSlots(after ?? 0, take ?? 50, includeArchived ?? false)));
        group.MapGet("/worker-slots/{id}", async (string id, ControlStore store) => await Execute(() => store.WorkerSlot(id)));
        group.MapPost("/worker-slots", async (CreateWorkerSlotInput input, ControlStore store) => await Execute(() => store.CreateWorkerSlot(input)));
        group.MapPost("/worker-slots/{id}/archive", async (string id, ArchiveInventoryInput input, ControlStore store) => await Execute(() => store.ArchiveWorkerSlot(id, input)));
        group.MapGet("/task-bindings", async (long? after, int? take, bool? includeReleased, ControlStore store) =>
            await Execute(() => store.TaskBindings(after ?? 0, take ?? 50, includeReleased ?? false)));
        group.MapGet("/task-bindings/{id}", async (string id, ControlStore store) => await Execute(() => store.TaskBinding(id)));
        group.MapPost("/task-bindings", async (CreateTaskBindingInput input, ControlStore store) => await Execute(() => store.CreateTaskBinding(input)));
        group.MapPost("/task-bindings/{id}/release", async (string id, ReleaseTaskBindingInput input, ControlStore store) => await Execute(() => store.ReleaseTaskBinding(id, input)));
    }

    private static async Task<IResult> Execute<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (InventoryException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status); }
        catch (ControlException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
    }
}
