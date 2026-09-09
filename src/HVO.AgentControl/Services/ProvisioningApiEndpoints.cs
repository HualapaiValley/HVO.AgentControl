using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Provisioning;

namespace HVO.AgentControl.Services;

public static class ProvisioningApiEndpoints
{
    // This group inherits owner authentication and CSRF validation. It records intent
    // only; no host runner, executable path, Docker socket or credential is injectable.
    public static void MapProvisioningApi(this RouteGroupBuilder ownerApi)
    {
        var group = ownerApi.MapGroup("/provisioning/operations");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (InventoryException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status); }
        });
        group.MapGet("", (long? after, int? take, ControlStore store) => store.ProvisionOperations(after ?? 0, take ?? 50));
        group.MapGet("/{id}", (string id, ControlStore store) => store.ProvisionOperation(id));
        group.MapPost("", async (CreateProvisionOperationInput input, ControlStore store) =>
        {
            var operation = await store.CreateProvisionOperation(input);
            return Results.Accepted($"/api/v1/provisioning/operations/{operation.Id}", operation);
        });
        group.MapPost("/{id}/cancel", (string id, ProvisionOperationControlInput input, ControlStore store) => store.CancelProvisionOperation(id, input));
        group.MapPost("/{id}/reconcile", (string id, ProvisionOperationControlInput input, ControlStore store) => store.RequestProvisionReconciliation(id, input));
        group.MapPost("/{id}/capacity", (string id, BindProvisionCapacityInput input, ControlStore store) => store.BindProvisionCapacity(id, input));
    }
}
