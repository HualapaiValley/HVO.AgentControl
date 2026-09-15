using HVO.AgentControl.Components;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Security.Cryptography;
using System.Text;

// The controller-private store, its WAL/SHM sidecars and the writer lock must be
// controller-only from the moment they are created. The process creation mask is
// narrowed before any file work so SQLite never creates a group/world-readable
// temporary of the database; the store still applies explicit 0600 modes.
HVO.AgentControl.Organization.OrganizationStore.RestrictProcessFileCreation();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddAcpControlHost(builder.Configuration);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

// A binding failure is a client 400, never a 500. Without this, Development
// rethrows BadHttpRequestException and the exception handler would mask a
// malformed request as a server fault.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var controlEnabled = builder.Configuration.GetValue<bool>("Control:Enabled");
var ownerPassword = Program.ResolveOwnerPassword(
    builder.Configuration["Control:OwnerPasswordFile"],
    controlEnabled);
var ownerAuthConfigured = ownerPassword is not null;

// RFC 9457 ProblemDetails is the single error contract. Instance and traceId
// are always present and the instance is the request path (no query string).
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    };
});

// Built-in OpenAPI JSON at /openapi/v1.json (no Swagger UI). The document is a
// contract for external tools and is served behind owner Basic auth when one is
// configured, matching the rest of the API surface.
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "HVO AgentControl API";
        document.Info.Version = HVO.AgentControl.ApplicationVersion.Current;
        document.Info.Description =
            "Control-host surface for the owned OpenCode ACP session and attached TUI. " +
            "Errors follow RFC 9457 ProblemDetails.";

        if (ownerAuthConfigured)
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["basic"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "basic",
                Description = "HTTP Basic with the owner account and the configured owner password.",
            };
        }

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, _) =>
    {
        // Every path except process liveness sits behind owner Basic auth when
        // it is configured.
        if (ownerAuthConfigured
            && !string.Equals(context.Description.RelativePath, "health/live", StringComparison.OrdinalIgnoreCase))
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("basic", context.Document, null)] = [],
            });
        }

        return Task.CompletedTask;
    });
});

var app = builder.Build();

// Sanitized 500s: never surface exception messages or stack traces, even in the
// Development environment where the developer exception page would otherwise
// leak. The handler is registered before the host's own middleware, so it wraps
// every downstream failure.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    var problemDetails = new ProblemDetails
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "An unexpected error occurred.",
        Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
        // The default writer applies CustomizeProblemDetails, but a client that
        // declines application/problem+json makes TryWriteAsync return false.
        // Populate the required contract fields before either write path so the
        // fallback still carries instance and traceId.
        Instance = context.Request.Path,
    };
    problemDetails.Extensions["traceId"] =
        System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

    var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
    if (!await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
        HttpContext = context,
        ProblemDetails = problemDetails,
    }))
    {
        // The explicit media type stops WriteAsJsonAsync from downgrading the
        // response to application/json.
        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }
}));

// Converts status-only responses (route 404s, data-binding 400s, and the
// terminal's raw status codes) into the same ProblemDetails contract.
app.UseStatusCodePages(async statusCodeContext =>
{
    var httpContext = statusCodeContext.HttpContext;
    await Results.Problem(statusCode: httpContext.Response.StatusCode).ExecuteAsync(httpContext);
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path != "/health/live" && ownerPassword is not null)
    {
        var authenticated = false;
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var supplied = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                authenticated = CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                    SHA256.HashData(Encoding.UTF8.GetBytes("owner:" + ownerPassword)));
            }
            catch (FormatException) { }
        }
        if (!authenticated)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"AgentControl\", charset=\"UTF-8\"";
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication required.",
                detail: "Provide HTTP Basic credentials for the owner account.")
                .ExecuteAsync(context);
            return;
        }
    }
    await next();
});
app.UseWebSockets();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>();
app.MapOpenApi();

app.MapGet("/api/info", () => Results.Ok(new InfoResponse(
    "HVO.AgentControl",
    2,
    "control-portal",
    WorkerControlImplemented: false)))
    .WithName("GetInfo")
    .WithTags("Control")
    .WithSummary("Describes the control-host baseline.")
    .Produces<InfoResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapGet("/api/control", (AcpControlHost host) => Results.Ok(host.GetStatus()))
    .WithName("GetControlStatus")
    .WithTags("Control")
    .WithSummary("Returns an immutable snapshot of the owned runtime.")
    .Produces<ControlStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapGet("/api/organization", (AcpControlHost host) =>
{
    var store = host.Organization;
    if (store is null)
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The control runtime is disabled or the authoritative store has not opened.");
    try
    {
        return Results.Ok(store.GetOverview());
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The authoritative store could not be read.");
    }
})
    .WithName("GetOrganization")
    .WithTags("Organization")
    .WithSummary("Returns the minimal organization overview from the authoritative store.")
    .Produces<HVO.AgentControl.Organization.OrganizationOverview>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPatch("/api/organization", (HttpContext context, AcpControlHost host, OrganizationUpdate update) =>
{
    if (!TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(),
            context.Request.Scheme, context.Request.Host.Value))
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cross-origin request rejected.",
            detail: "Organization updates must originate from the portal origin.");
    if (string.IsNullOrWhiteSpace(update.OrganizationId))
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization update.",
            detail: "A stable organization id is required.");
    if (update.Revision is null or < 1)
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization update.",
            detail: "The current organization revision is required for a safe update.");
    try
    {
        return Results.Ok(host.RenameOrganization(update.OrganizationId, update.DisplayName ?? string.Empty, update.Revision.Value));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization update.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Organization update conflicted.",
            detail: "The organization changed since it was read. Reload and retry.");
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found.",
            detail: "No organization with that stable id exists.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The authoritative store could not be updated.");
    }
})
    .WithName("UpdateOrganization")
    .WithTags("Organization")
    .WithSummary("Renames the organization display name under an optimistic revision check.")
    .Accepts<OrganizationUpdate>("application/json")
    .Produces<HVO.AgentControl.Organization.OrganizationOverview>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPut("/api/organization/basic-instructions", (
    HttpContext context,
    AcpControlHost host,
    OrganizationInstructionsUpdate update) =>
{
    if (!TerminalProtocol.IsSameOrigin(
            context.Request.Headers.Origin.ToString(),
            context.Request.Scheme,
            context.Request.Host.Value))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cross-origin request rejected.",
            detail: "Instruction updates must originate from the portal origin.");
    }

    if (string.IsNullOrWhiteSpace(update.OrganizationId) || update.Revision is null or < 1)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid instruction update.",
            detail: "A stable organization id and current revision are required.");
    }

    try
    {
        return Results.Ok(host.UpdateOrganizationBasicInstructions(
            update.OrganizationId,
            update.BasicInstructions ?? string.Empty,
            update.Revision.Value));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Invalid basic instructions.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found.",
            detail: "No organization with that stable id exists.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Instruction update conflicted.",
            detail: "The organization changed since it was read. Reload and retry.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The basic instructions could not be updated.");
    }
})
    .WithName("UpdateOrganizationBasicInstructions")
    .WithTags("Organization")
    .WithSummary("Updates persisted basic instructions and marks current orientation stale.")
    .Produces<HVO.AgentControl.Organization.OrganizationOverview>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPut("/api/roles/{id}/instructions", (
    HttpContext context,
    AcpControlHost host,
    string id,
    RoleInstructionsUpdate update) =>
{
    if (Program.RejectCrossOrigin(context, "Role instruction updates") is { } rejection)
    {
        return rejection;
    }

    if (string.IsNullOrWhiteSpace(id) || update.Revision is null or < 1)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid role instruction update.",
            detail: "A stable role id and current role revision are required.");
    }

    try
    {
        return Results.Ok(host.UpdateRoleInstructions(
            id,
            update.StandingInstructions ?? string.Empty,
            update.Revision.Value));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Invalid role instructions.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Role not found.",
            detail: "No role with that stable id exists.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Role instruction update conflicted.",
            detail: "The role changed since it was read. Reload and retry.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The role instructions could not be updated.");
    }
})
    .WithName("UpdateRoleInstructions")
    .WithTags("Organization")
    .WithSummary("Updates authoritative role standing instructions under optimistic revision and marks affected orientation stale.")
    .Produces<HVO.AgentControl.Organization.OrganizationOverview>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/orientation", (AcpControlHost host) =>
{
    try
    {
        return Results.Ok(host.GetOrientationStatus());
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Orientation unavailable.",
            detail: "The current employee orientation could not be read.");
    }
})
    .WithName("GetOrientation")
    .WithTags("Orientation")
    .WithSummary("Returns employee orientation and dispatch-hold state.")
    .Produces<HVO.AgentControl.Organization.OrientationStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/orientation/deliver", (HttpContext context, AcpControlHost host) =>
{
    if (Program.RejectCrossOrigin(context, "Orientation delivery") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(host.RecomposeAndDeliverOrientation());
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Orientation changed.",
            detail: "Reload and retry delivery.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Orientation unavailable.",
            detail: "Orientation could not be delivered.");
    }
})
    .WithName("DeliverOrientation")
    .WithTags("Orientation")
    .WithSummary("Recomposes and atomically delivers current standing orientation.")
    .Produces<HVO.AgentControl.Organization.OrientationStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/orientation/comprehension", (
    HttpContext context,
    AcpControlHost host,
    HVO.AgentControl.Organization.OrientationEvidenceRequest evidence) =>
{
    if (Program.RejectCrossOrigin(context, "Orientation evidence") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(host.RecordOrientationEvidence(evidence));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid orientation evidence.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Orientation evidence rejected.",
            detail: "The assignment is stale, duplicated, or no longer delivered.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Orientation unavailable.",
            detail: "Evidence could not be recorded.");
    }
})
    .WithName("RecordOrientationEvidence")
    .WithTags("Orientation")
    .WithSummary("Records owner-submitted structured evidence validated against exact persisted orientation facts. This is an owner override record and host validation, not a live model demonstration.")
    .Produces<HVO.AgentControl.Organization.OrientationStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/orientation/comprehension/run", async (HttpContext context, AcpControlHost host) =>
{
    if (Program.RejectCrossOrigin(context, "The live comprehension action") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(await host.RunOrientationComprehensionAsync(context.RequestAborted));
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Comprehension unavailable.",
            detail: "A current delivered orientation and idle demonstration slot are required.");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Comprehension cancelled.",
            detail: "The caller cancelled the live demonstration.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Orientation unavailable.",
            detail: "The comprehension result could not be persisted.");
    }
})
    .WithName("RunOrientationComprehension")
    .WithTags("Orientation")
    .WithSummary("Runs one owner-triggered bounded ACP JSON comprehension demonstration without tools. Model, ACP, malformed, empty, oversized and non-terminal outcomes are persisted as live-model failures.")
    .Produces<HVO.AgentControl.Organization.OrientationStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPut("/api/orientation/manual-hold", (HttpContext context, AcpControlHost host, ManualHoldUpdate update) =>
{
    if (Program.RejectCrossOrigin(context, "Dispatch holds") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(host.SetManualDispatchHold(update.Held, update.Detail));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid dispatch hold.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Orientation unavailable.",
            detail: "The dispatch hold could not be changed.");
    }
})
    .WithName("SetManualDispatchHold")
    .WithTags("Orientation")
    .WithSummary("Sets or clears the owner's independent manual dispatch hold.")
    .Produces<HVO.AgentControl.Organization.OrientationStatus>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/permissions/grants", (
    HttpContext context,
    AcpControlHost host,
    HVO.AgentControl.Organization.PermissionGrantRequest grant) =>
{
    if (Program.RejectCrossOrigin(context, "Permission grants") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(host.GrantPermission(grant));
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Invalid permission grant.",
            detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Permission grant conflicted.",
            detail: "The policy revision, expiry, or idempotency record changed.");
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Grant target not found.",
            detail: "The employee or active restriction does not exist in the organization.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Permission store unavailable.",
            detail: "The staged grant could not be recorded.");
    }
})
    .WithName("CreatePermissionGrant")
    .WithTags("Permissions")
    .WithSummary("Persists and audits a staged owner-approved grant for an explicitly waivable restriction. Phase 1 grants are not executable through inbound ACP permission requests.")
    .Produces<HVO.AgentControl.Organization.PermissionGrantSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/permissions/grants/{id}/revoke", (
    HttpContext context,
    AcpControlHost host,
    string id,
    GrantRevokeRequest revoke) =>
{
    if (Program.RejectCrossOrigin(context, "Permission revocation") is { } rejection)
    {
        return rejection;
    }

    try
    {
        return Results.Ok(host.RevokePermission(id, revoke.ExpectedRevision));
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Permission grant not found.",
            detail: "No grant with that stable id exists.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Permission revocation conflicted.",
            detail: "The grant changed or was already revoked.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Permission store unavailable.",
            detail: "The grant could not be revoked.");
    }
})
    .WithName("RevokePermissionGrant")
    .WithTags("Permissions")
    .WithSummary("Revokes a staged owner-approved permission grant under optimistic revision.")
    .Produces<HVO.AgentControl.Organization.PermissionGrantSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/control/model", async (HttpContext context, AcpControlHost host, ModelSelection selection) =>
{
    if (!TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(),
            context.Request.Scheme, context.Request.Host.Value))
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cross-origin request rejected.",
            detail: "Model changes must originate from the portal origin.");
    if (host.GetStatus().State != "ready")
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runtime not ready.",
            detail: "The control runtime must be ready before a model change.");
    if (string.IsNullOrWhiteSpace(selection.Model) || selection.Model.Length > 256)
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid model selection.",
            detail: "Select an advertised provider/model.");
    if (!host.GetStatus().ModelSyncSupported)
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Model synchronization unsupported.",
            detail: "The attached OpenCode TUI does not support synchronized model selection yet. Use the TUI picker; the displayed server model updates after submission.");
    try
    {
        if (!await host.SetModelAsync(selection.Model, context.RequestAborted))
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Model change could not be confirmed.",
                detail: "Check the session before retrying.");
        return Results.Ok(new ModelResponse(host.GetStatus().Model));
    }
    catch (ArgumentException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid model selection.",
            detail: "Select an advertised provider/model.");
    }
})
    .WithName("SetControlModel")
    .WithTags("Control")
    .WithSummary("Changes the model for the established session.")
    .Accepts<ModelSelection>("application/json")
    .Produces<ModelResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status502BadGateway);

app.MapPost("/api/control/cancel", async (HttpContext context, AcpControlHost host) =>
{
    if (!TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(),
            context.Request.Scheme, context.Request.Host.Value))
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cross-origin request rejected.",
            detail: "Cancellation must originate from the portal origin.");
    if (!host.GetStatus().CanControl)
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runtime unavailable.",
            detail: "An established control session is required before cancellation.");
    if (!await host.CancelAsync(context.RequestAborted))
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Cancellation not accepted.",
            detail: "The owned session is not available to cancel.");
    return Results.Accepted(value: new CancelResponse("cancel-requested", Completed: false));
})
    .WithName("CancelControlTurn")
    .WithTags("Control")
    .WithSummary("Requests cancellation of the active session turn.")
    .Produces<CancelResponse>(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.Map("/terminal", async (HttpContext context, AcpControlHost host) =>
{
    var status = host.GetStatus();
    if (!status.CanControl || !status.TerminalReady)
    {
        context.Response.StatusCode = 503;
        return;
    }
    await TerminalEndpoint.HandleAsync(
        context,
        Path.Combine(host.DataDirectory, "home"),
        host.TmuxSessionName,
        host.AgentLauncher);
})
    .WithName("AttachTerminal")
    .WithTags("Terminal")
    .WithSummary("Attaches a single owner viewer to the owned tmux session.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Process liveness only; deliberately unauthenticated and dependency-free.
app.MapGet("/health/live", () => Results.Ok(new HealthResponse("healthy")))
    .WithName("HealthLive")
    .WithTags("Health")
    .WithSummary("Process liveness. Does not probe the runtime or any dependency.")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

// Readiness of the exact owned ACP session and attached TUI. It is not a
// database, worker, or provider check and must not claim one.
app.MapGet("/health/ready", (AcpControlHost host) =>
{
    var status = host.GetStatus();
    return IsRuntimeReady(status)
        ? Results.Ok(new HealthResponse("ready"))
        : Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Runtime not ready.",
            detail: $"The owned OpenCode session and attached TUI are not ready (state: {status.State}).");
})
    .WithName("HealthReady")
    .WithTags("Health")
    .WithSummary("Readiness of the owned ACP session and attached TUI.")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/version", () => Results.Ok(new VersionResponse(
    HVO.AgentControl.ApplicationVersion.Current,
    "OpenCode",
    "ACP",
    "Docker")))
    .WithName("GetVersion")
    .WithTags("Control")
    .WithSummary("Reports the application and harness versions.")
    .Produces<VersionResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.Run();

public partial class Program
{
    /// <summary>Minimum length required of a configured owner password, after trimming.</summary>
    public const int MinimumOwnerPasswordLength = 24;

    /// <summary>
    /// Returns a ProblemDetails 403 when a state-changing request did not come
    /// from the portal origin, or null when the request may proceed. Centralized
    /// so every mutating endpoint applies the identical same-origin contract.
    /// </summary>
    internal static IResult? RejectCrossOrigin(HttpContext context, string operation)
    {
        if (HVO.AgentControl.Terminal.TerminalProtocol.IsSameOrigin(
                context.Request.Headers.Origin.ToString(),
                context.Request.Scheme,
                context.Request.Host.Value))
        {
            return null;
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cross-origin request rejected.",
            detail: $"{operation} must originate from the portal origin.");
    }

    /// <summary>
    /// Resolves the configured owner password for the startup auth gate.
    /// </summary>
    /// <remarks>
    /// Reads and validates <paramref name="passwordFile"/> independently of the
    /// runtime switch. A configured file must be readable and contain at least
    /// <see cref="MinimumOwnerPasswordLength"/> trimmed characters; a blank or
    /// short file is rejected even when the runtime is disabled, because a
    /// configured credential is always expected to be usable. When no file is
    /// configured, the runtime-enabled mode still requires one, while a disabled
    /// runtime may run without owner auth for local development. Every failure is
    /// a sanitized <see cref="InvalidOperationException"/> that never includes the
    /// file contents, so calling this before <c>builder.Build()</c> fails closed
    /// before any runtime or model provider starts.
    /// </remarks>
    public static string? ResolveOwnerPassword(string? passwordFile, bool controlEnabled)
    {
        if (string.IsNullOrWhiteSpace(passwordFile))
        {
            if (controlEnabled)
            {
                throw new InvalidOperationException(
                    "Control:OwnerPasswordFile must be configured with a readable file containing at least "
                    + $"{MinimumOwnerPasswordLength} characters when the runtime is enabled.");
            }

            return null;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(passwordFile);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "The configured Control:OwnerPasswordFile could not be read. Refusing to start with an unverifiable owner password.",
                exception);
        }

        var password = contents.Trim();
        if (password.Length < MinimumOwnerPasswordLength)
        {
            throw new InvalidOperationException(
                $"The configured Control:OwnerPasswordFile must contain at least {MinimumOwnerPasswordLength} "
                + "non-whitespace characters; it is blank or too short.");
        }

        return password;
    }

    /// <summary>
    /// Pure readiness predicate for the owned OpenCode ACP session and attached
    /// TUI. Terminal readiness is required regardless of whether the terminal
    /// launcher is configured, so a runtime started with
    /// <c>Control:EnableTerminal=false</c> is never reported ready by
    /// <c>/health/ready</c>.
    /// </summary>
    /// <remarks>
    /// Readiness fails closed: an unknown native session state is not ready, and
    /// the check never probes a database, worker, or model provider.
    /// </remarks>
    public static bool IsRuntimeReady(ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var sessionStateKnown = string.Equals(status.SessionState, "idle", StringComparison.Ordinal)
            || string.Equals(status.SessionState, "busy", StringComparison.Ordinal);

        return string.Equals(status.State, "ready", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(status.SessionId)
            && sessionStateKnown
            && status.TerminalReady;
    }
}

public sealed record ModelSelection(string? Model);

public sealed record OrganizationUpdate(string? OrganizationId, string? DisplayName, int? Revision);
public sealed record OrganizationInstructionsUpdate(string? OrganizationId, string? BasicInstructions, int? Revision);
public sealed record RoleInstructionsUpdate(string? StandingInstructions, int? Revision);
public sealed record ManualHoldUpdate(bool Held, string? Detail);
public sealed record GrantRevokeRequest(int ExpectedRevision);

public sealed record InfoResponse(string Name, int Generation, string Status, bool WorkerControlImplemented);

public sealed record ModelResponse(string Model);

public sealed record CancelResponse(string Status, bool Completed);

public sealed record HealthResponse(string Status);

public sealed record VersionResponse(string Version, string AgentHarness, string ControlProtocol, string ExecutionEnvironment);
