using System.Security.Claims;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Provisioning;

namespace HVO.AgentControl.Services;

public static class HostResourceApiEndpoints
{
    // Called on the authenticated/CSRF-protected API group, never on the bare app.
    public static void MapHostResourceOwnerApi(this RouteGroupBuilder ownerApi)
    {
        ownerApi.MapGet("/host-executors", (string? hostId, ControlStore store) => store.HostExecutors(hostId));
        ownerApi.MapPost("/host-executors", async (CreateHostExecutorInput input, ControlStore store) =>
        {
            var enrollment = await store.CreateHostExecutor(input);
            return Results.Created($"/api/v1/host-executors/{enrollment.Id}", enrollment);
        });
        ownerApi.MapPost("/host-executors/{id}/rotate", (string id, RotateHostExecutorInput input, ControlStore store) =>
            store.RotateHostExecutor(id, input));
        ownerApi.MapPost("/host-executors/{id}/suspend", (string id, ChangeHostExecutorStateInput input, ControlStore store) =>
            store.ChangeHostExecutorState(id, input, HostExecutorState.Suspended));
        ownerApi.MapPost("/host-executors/{id}/revoke", (string id, ChangeHostExecutorStateInput input, ControlStore store) =>
            store.ChangeHostExecutorState(id, input, HostExecutorState.Revoked));
        ownerApi.MapPost("/host-resource-policies", (ConfigureHostResourcePolicyInput input, ControlStore store) =>
            store.ConfigureHostResourcePolicy(input));
        ownerApi.MapGet("/hosts/{id}/capacity", (string id, ControlStore store) => store.HostCapacity(id));
        ownerApi.MapGet("/host-resource-reservations", (string? physicalHostId, ControlStore store) =>
            store.HostResourceReservations(physicalHostId));
        ownerApi.MapPost("/host-resource-reservations", async (AcquireHostResourceReservationInput input, ControlStore store) =>
        {
            var reservation = await store.AcquireHostResourceReservation(input);
            return Results.Created($"/api/v1/host-resource-reservations/{reservation.Id}", reservation);
        });
        ownerApi.MapPost("/host-resource-reservations/{id}/unknown", (string id, MarkHostResourceUnknownInput input, ControlStore store) =>
            store.MarkHostResourceUnknown(id, input));
        ownerApi.MapPost("/host-resource-reservations/{id}/release", (string id, ReleaseHostResourceReservationInput input, ControlStore store) =>
            store.ReleaseHostResourceReservation(id, input));
    }

    public static void MapHostExecutorApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/host-executor").DisableAntiforgery();
        group.MapPost("/activate", (ClaimsPrincipal user, ActivateHostExecutorInput input, ControlStore store) =>
                store.ActivateHostExecutor(Principal(user), input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.EnrollmentPolicy);
        group.MapPost("/observations", (ClaimsPrincipal user, SubmitHostResourceObservationInput input, ControlStore store) =>
                store.SubmitHostResourceObservation(Principal(user), input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/reservations/{id}/begin-effect", (ClaimsPrincipal user, string id, BeginHostResourceEffectInput input, ControlStore store) =>
                store.BeginHostResourceEffect(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/provisioning/{id}/authority", (ClaimsPrincipal user, string id, ApproveHostProvisionAuthorityInput input, ControlStore store) =>
                store.ApproveHostProvisionAuthority(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/provisioning/{id}/claim", (ClaimsPrincipal user, string id, ClaimHostProvisioningInput input, ControlStore store) =>
                store.ClaimHostProvisioningAssignment(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapGet("/provisioning/{id}", (ClaimsPrincipal user, string id, ControlStore store) =>
                store.HostProvisioningAssignment(Principal(user), id))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/provisioning/{id}/begin-effect", (ClaimsPrincipal user, string id, BeginHostProvisionEffectInput input, ControlStore store) =>
                store.BeginHostProvisionEffect(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/provisioning/{id}/progress", (ClaimsPrincipal user, string id, HostProvisionProgressInput input, ControlStore store) =>
                store.RecordHostProvisionProgress(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
        group.MapPost("/provisioning/{id}/result", (ClaimsPrincipal user, string id, HostProvisionResultInput input, ControlStore store) =>
                store.RecordHostProvisionResult(Principal(user), id, input))
            .RequireAuthorization(HostExecutorAuthenticationDefaults.ActivePolicy);
    }

    private static HostExecutorPrincipal Principal(ClaimsPrincipal user) => new(
        Required(user, ClaimTypes.NameIdentifier),
        Required(user, HostExecutorAuthenticationDefaults.HostIdClaim),
        int.Parse(Required(user, HostExecutorAuthenticationDefaults.GenerationClaim), System.Globalization.CultureInfo.InvariantCulture),
        Required(user, HostExecutorAuthenticationDefaults.StateClaim),
        Required(user, HostExecutorAuthenticationDefaults.CredentialDigestClaim));

    private static string Required(ClaimsPrincipal user, string type) =>
        user.FindFirstValue(type) ?? throw new ControlException("Host executor authentication failed.", 401);
}
