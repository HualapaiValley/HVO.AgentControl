using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Services;

public static class InventoryApiEndpoints
{
    // Called on the authenticated/CSRF-protected API group, never on the bare app.
    public static void MapInventoryApi(this RouteGroupBuilder ownerApi)
    {
        var group = ownerApi.MapGroup("");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (InventoryException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status); }
        });
        group.MapGet("/hosts", (long? after, int? take, bool? includeArchived, ControlStore store) => store.Hosts(after ?? 0, take ?? 50, includeArchived ?? false));
        group.MapGet("/hosts/{id}", (string id, ControlStore store) => store.Host(id));
        group.MapPost("/hosts", async (CreateHostInput input, ControlStore store) =>
        {
            var host = await store.CreateHost(input);
            return Results.Created($"/api/v1/hosts/{host.Id}", host);
        });
        group.MapPut("/hosts/{id}", (string id, UpdateHostInput input, ControlStore store) => store.UpdateHost(id, input));
        group.MapPost("/hosts/{id}/archive", (string id, ArchiveInventoryInput input, ControlStore store) => store.ArchiveHost(id, input));
        group.MapGet("/projects", (long? after, int? take, bool? includeArchived, ControlStore store) => store.Projects(after ?? 0, take ?? 50, includeArchived ?? false));
        group.MapGet("/projects/by-repository", (string repositoryUrl, ControlStore store) => store.ProjectByRepository(repositoryUrl));
        group.MapGet("/projects/{id}", (string id, ControlStore store) => store.Project(id));
        group.MapPost("/projects", async (CreateProjectInput input, ControlStore store) =>
        {
            var project = await store.CreateProject(input);
            return Results.Created($"/api/v1/projects/{project.Id}", project);
        });
        group.MapPut("/projects/{id}", (string id, UpdateProjectInput input, ControlStore store) => store.UpdateProject(id, input));
        group.MapPost("/projects/{id}/archive", (string id, ArchiveInventoryInput input, ControlStore store) => store.ArchiveProject(id, input));
        group.MapGet("/inventory/requests/{id}", (string id, ControlStore store) => store.InventoryMutation(id));
    }
}
