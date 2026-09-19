using HVO.AgentControl.Components;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Terminal;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
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
builder.Services.AddRemoteWorkerControl(builder.Configuration);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

// A binding failure is a client 400, never a 500. Without this, Development
// rethrows BadHttpRequestException and the exception handler would mask a
// malformed request as a server fault.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var controlEnabled = builder.Configuration.GetValue<bool>("Control:Enabled");
var workerControlEnabled = builder.Configuration.GetValue<bool>("WorkerControl:Enabled");
var ownerPassword = Program.ResolveOwnerPassword(
    builder.Configuration["Control:OwnerPasswordFile"],
    controlEnabled || workerControlEnabled);
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

// Converts status-only API, OpenAPI, and terminal responses (including the
// terminal's raw 400/404/503 statuses and framework-generated 405s) into the
// same ProblemDetails contract. Portal routes keep their endpoint-owned HTML;
// the NotFoundPage catch-all writes a friendly document and sets 404 itself.
app.UseStatusCodePages(async statusCodeContext =>
{
    var httpContext = statusCodeContext.HttpContext;
    if (Program.IsMachineContractPath(httpContext.Request.Path))
    {
        await Program.WriteProblemAsync(httpContext, httpContext.Response.StatusCode);
    }
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

// Select an endpoint before antiforgery runs so unmatched machine-contract
// paths cannot fall through to the Razor catch-all. The middleware below is
// populated from the final endpoint data sources after all routes are mapped.
app.UseRouting();
var machineContractRoutes = new Program.MachineContractRouteCatalog();
app.Use(async (context, next) =>
{
    // Normal API/health endpoints, including framework method and content-type
    // rejection endpoints, execute without a catalog scan. Only a machine path
    // that routing would otherwise hand to the Razor portal catch-all needs the
    // cached endpoint metadata fallback.
    if (Program.IsUnknownOpenApiPath(context.Request.Path))
    {
        await Program.WriteProblemAsync(context, StatusCodes.Status404NotFound);
        return;
    }

    if (Program.IsMachineContractPath(context.Request.Path)
        && (context.GetEndpoint() is not RouteEndpoint
            || Program.IsPortalCatchAll(context.GetEndpoint())
            || Program.IsFrameworkMethodNotAllowed(context.GetEndpoint()))
        && machineContractRoutes.Classify(context) is { } rejection)
    {
        if (rejection.AllowedMethods.Count > 0)
        {
            context.Response.Headers.Allow = string.Join(", ", rejection.AllowedMethods);
        }

        await Program.WriteProblemAsync(context, rejection.StatusCode);
        return;
    }

    await next();

    // Framework-generated 405 responses do not consistently advertise a
    // truthful Allow set. Machine routes use their cached endpoint metadata;
    // portal component routes support GET only and deliberately do not claim
    // implicit HEAD support.
    if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
    {
        if (Program.IsMachineContractPath(context.Request.Path)
            && machineContractRoutes.Classify(context) is { StatusCode: StatusCodes.Status405MethodNotAllowed } methodRejection)
        {
            context.Response.Headers.Allow = string.Join(", ", methodRejection.AllowedMethods);
        }
        else if (Program.IsKnownPortalGetPath(context.Request.Path))
        {
            context.Response.Headers.Allow = HttpMethods.Get;
        }
    }
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>();
app.MapOpenApi();

// Environment-only fault endpoint used by the sanitized exception contract
// tests. It is not mapped in Development, Production, or any normal runtime.
if (app.Environment.IsEnvironment("ExceptionPathTests"))
{
    app.MapGet("/__test/fault", (HttpContext _) => throw new InvalidOperationException("injected-sensitive-failure-detail"));
}

// Worker-control capability truth (2026-09-18, #217 acceptance). These flags
// distinguish three different claims and must not be collapsed:
//   CodeAvailable           - the code exists in this build.
//   Implemented             - the first managed disposable two-host path is
//                             implemented end to end: pinned ED25519/strict SSH,
//                             enrolled worker, ACP session lifecycle, provider
//                             prompt/tools, cancellation, disconnect/reconnect
//                             reconciliation, stale-lease fencing, bounded
//                             replay, worker restart and viewer attach were
//                             exercised on an authorized disposable topology.
//   OperationallyValidated  - that same first path was validated live and the
//                             disposable resources were cleaned up.
// Implemented/validated cover only that first path. Key rotation and compromise
// re-enrollment, and production managed hires/provisioning, are NOT validated
// by these flags. Enabled remains the effective deployment/configuration gate
// and stays false by default; the live acceptance ran an explicitly enabled,
// disposable isolation. WorkerControlValidatedScope carries the exact bound in
// band so a client can distinguish the accepted path from the excluded ones.
app.MapGet("/api/info", () => Results.Ok(new InfoResponse(
    "HVO.AgentControl",
    2,
    "control-portal",
    WorkerControlImplemented: true,
    WorkerControlCodeAvailable: true,
    WorkerControlEnabled: workerControlEnabled,
    WorkerControlOperationallyValidated: true,
    WorkerControlValidatedScope: Program.WorkerControlValidatedScope)))
    .WithName("GetInfo")
    .WithTags("Control")
    .WithSummary("Describes the control-host baseline.")
    .WithDescription(
        "Implementation identity plus worker-control capability truth. " +
        "workerControlImplemented and workerControlOperationallyValidated " +
        $"describe only the scope named by workerControlValidatedScope " +
        $"(\"{Program.WorkerControlValidatedScope}\"): the first managed disposable " +
        "two-host path (pinned strict SSH, enrolled worker, ACP session lifecycle, " +
        "provider prompt/tools, cancellation, disconnect/reconnect reconciliation, " +
        "stale-lease fencing, bounded replay, worker restart and viewer attach on an " +
        "authorized disposable topology). They explicitly exclude key rotation and " +
        "compromise re-enrollment and production managed hiring/provisioning. " +
        "workerControlEnabled is the separate deployment/configuration gate and is " +
        "false in the default configuration.")
    .Produces<InfoResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapGet("/api/execution-hosts", (AcpControlHost control, ExecutionHostRegistry registry) =>
{
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(registry.List()); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("ListExecutionHosts").WithTags("Remote workers")
    .WithSummary("Lists configured-reference execution-host registrations without credential bytes.");

app.MapPost("/api/execution-hosts", (HttpContext context, AcpControlHost control, ExecutionHostRegistry registry, RegisterExecutionHostRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Execution-host registration") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(registry.Register(request)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("RegisterExecutionHost").WithTags("Remote workers")
    .WithSummary("Registers one owner-configured allowlisted host reference; arbitrary endpoints and paths are not accepted.");

app.MapPost("/api/execution-hosts/{id}/probe", async (HttpContext context, AcpControlHost control, ExecutionHostRegistry registry, string id, ProbeExecutionHostRequest request, CancellationToken cancellationToken) =>
{
    if (Program.RejectCrossOrigin(context, "Execution-host probing") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await registry.ProbeAsync(id, request.ExpectedRevision, cancellationToken)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("ProbeExecutionHost").WithTags("Remote workers")
    .WithSummary("Runs the fixed approved-host capability probe only when WorkerControl is explicitly enabled.");

app.MapPost("/api/execution-hosts/{id}/disable", (HttpContext context, AcpControlHost control, ExecutionHostRegistry registry, string id, DisableExecutionHostRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Execution-host disable") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(registry.Disable(id, request.ExpectedRevision)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("DisableExecutionHost").WithTags("Remote workers");

app.MapGet("/api/workers/status", (AcpControlHost control) =>
{
    if (control.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(new { enrollments = store.ListWorkerEnrollments(), cursors = store.ListWorkerCursors(), pendingWorkerPermissions = store.ListWorkerPendingPermissions(), recovery = store.ListWorkerRecoveryObligations(activeOnly: true), recoveryAudit = store.ListWorkerRecoveryAudit(), eventRetention = store.ListWorkerEventRetention() }); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("GetRemoteWorkerStatus").WithTags("Remote workers");

app.MapPost("/api/workers/enroll/plan", async (HttpContext context, AcpControlHost control, RemoteWorkerProvisioningCoordinator coordinator, WorkerEnrollPlanRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Worker enrollment planning") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await coordinator.PlanAsync(request.RuntimeBindingId, request.HostId, request.ImageDigest, context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("PlanRemoteWorkerEnrollment").WithTags("Remote workers");

app.MapPost("/api/workers/{workerId}/enroll/apply", async (HttpContext context, AcpControlHost control, RemoteWorkerProvisioningCoordinator coordinator, string workerId) =>
{
    if (Program.RejectCrossOrigin(context, "Worker enrollment application") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await coordinator.ApplyAllAsync(workerId, context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("ApplyRemoteWorkerEnrollment").WithTags("Remote workers");

app.MapPost("/api/workers/{workerId}/cleanup", async (HttpContext context, AcpControlHost control, RemoteWorkerProvisioningCoordinator coordinator, string workerId) =>
{
    if (Program.RejectCrossOrigin(context, "Worker cleanup") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { await coordinator.CleanupAsync(workerId, context.RequestAborted); return Results.Ok(); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("CleanupRemoteWorker").WithTags("Remote workers");

app.MapPost("/api/workers/request", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, WorkerPromptRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Worker dispatch") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await manager.DispatchAsync(new(request.EmployeeId, request.RuntimeBindingId, request.WorkerId, request.SessionRecordId, request.NativeSessionId, request.IdempotencyKey, request.Prompt), context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("DispatchRemoteWorkerRequest").WithTags("Remote workers");

app.MapPost("/api/workers/request/{requestId}/cancel", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, string requestId) =>
{
    if (Program.RejectCrossOrigin(context, "Worker cancellation") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await manager.CancelAsync(new(requestId), context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("CancelRemoteWorkerRequest").WithTags("Remote workers");

app.MapGet("/api/workers/{workerId}/permissions", (AcpControlHost control, string workerId) =>
{
    if (control.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        if (store.GetWorkerEnrollment(workerId) is null) return Results.Problem(statusCode: 404, title: "Worker not found.");
        return Results.Ok(store.ListWorkerPendingPermissions(workerId));
    }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("GetRemoteWorkerPermissions").WithTags("Remote workers");

app.MapPost("/api/workers/{workerId}/permission/reject", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, string workerId, WorkerPermissionRejectRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Worker permission rejection") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { await manager.RejectPermissionAsync(new(workerId, request.DecisionId, request.Revision), context.RequestAborted); return Results.Ok(); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("RejectRemoteWorkerPermission").WithTags("Remote workers");

app.MapPost("/api/workers/{workerId}/sync", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, string workerId) =>
{
    if (Program.RejectCrossOrigin(context, "Worker synchronization") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await manager.SynchronizeOnceAsync(workerId, context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("SynchronizeRemoteWorker").WithTags("Remote workers");

// Recovery invokes the exact worker-side reconciliation for the named obligation
// and clears the controller row only after the worker accepted it, so an operator
// action can never merely hide an unreconciled worker. Obligations whose real
// outcome cannot be established remotely are refused with 409 rather than cleared.
app.MapPost("/api/workers/{workerId}/recover", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, string workerId, WorkerRecoveryRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Worker recovery") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await manager.RecoverAsync(workerId, request.ObligationId, request.ExpectedRevision, context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("RecoverRemoteWorker").WithTags("Remote workers");

// Controller obligations acknowledged here cannot be cleared by remote protocol
// evidence alone. Marker-less replay/ownership obligations and marker-bearing
// session-reconciliation obligations require an authenticated owner to attest
// explicit external reconciliation with a fixed disposition and bounded SHA-256
// evidence reference, which is retained in the recovery audit.
app.MapPost("/api/workers/{workerId}/recover/{obligationId}/acknowledge", async (HttpContext context, AcpControlHost control, WorkerConnectionManager manager, string workerId, string obligationId, WorkerRecoveryAcknowledgementRequest request) =>
{
    if (Program.RejectCrossOrigin(context, "Worker recovery acknowledgement") is { } rejection) return rejection;
    if (control.Organization is null) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(await manager.AcknowledgeRecoveryAsync(workerId, obligationId, request.ExpectedRevision, request.EvidenceHash, request.Disposition, context.RequestAborted)); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
}).WithName("AcknowledgeRemoteWorkerRecovery").WithTags("Remote workers");

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

app.MapGet("/api/organization/portal", (AcpControlHost host, HVO.AgentControl.RemoteWorker.IRemoteWorkerStatusProvider remoteWorkers) =>
{
    var store = host.Organization;
    if (store is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The control runtime is disabled or the authoritative store has not opened.");
    }

    try
    {
        var result = HVO.AgentControl.Organization.PortalOrganizationReadModel.Build(
            store.GetOverview(),
            host.OrganizationIdentity,
            host.GetStatus(),
            remoteWorkers,
            store.ListHireRequests());
        return Results.Ok(result);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The authoritative store could not be read.");
    }
})
    .WithName("GetPortalOrganization")
    .WithTags("Organization")
    .WithSummary("Returns owner-facing organization diagnostics from the authoritative store and exact host status.")
    .Produces<HVO.AgentControl.Organization.PortalOrganizationOverview>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/employees/{id}", (AcpControlHost host, IRemoteWorkerStatusProvider remoteWorkers, string id) =>
{
    if (!Program.IsValidEmployeeId(id))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid employee id.",
            detail: "A bounded stable employee id is required.");
    }

    var store = host.Organization;
    if (store is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The control runtime is disabled or the authoritative store has not opened.");
    }

    try
    {
        var employee = HVO.AgentControl.Organization.PortalOrganizationReadModel.FindEmployee(
            store.GetOverview(),
            host.OrganizationIdentity,
            host.GetStatus(),
            id,
            remoteWorkers);
        return employee is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Employee not found.",
                detail: "No employee with that stable id exists.")
            : Results.Ok(employee);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The authoritative store could not be read.");
    }
})
    .WithName("GetEmployee")
    .WithTags("Organization")
    .WithSummary("Returns exact safe diagnostics for one employee selected by stable id.")
    .Produces<HVO.AgentControl.Organization.PortalEmployeeDetail>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/departments/{id}", (AcpControlHost host, IRemoteWorkerStatusProvider remoteWorkers, string id) =>
{
    if (!Program.IsValidDepartmentId(id))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid department id.",
            detail: "A bounded stable department id is required.");
    }

    var store = host.Organization;
    if (store is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The control runtime is disabled or the authoritative store has not opened.");
    }

    try
    {
        var department = HVO.AgentControl.Organization.PortalOrganizationReadModel.FindDepartment(
            store.GetOverview(),
            host.OrganizationIdentity,
            host.GetStatus(),
            id,
            remoteWorkers);
        return department is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Department not found.",
                detail: "No department with that stable id exists.")
            : Results.Ok(department);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization store unavailable.",
            detail: "The authoritative store could not be read.");
    }
})
    .WithName("GetDepartment")
    .WithTags("Organization")
    .WithSummary("Returns the authoritative roles, roster and scoped availability for one department selected by stable id.")
    .Produces<HVO.AgentControl.Organization.PortalDepartmentDetail>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/hire-requests", (AcpControlHost host) =>
{
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(store.ListHireRequests()); }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Hire requests unavailable.", detail: "The authoritative store could not be read.");
    }
})
    .WithName("ListHireRequests").WithTags("Hiring")
    .WithSummary("Lists durable owner hire requests; it does not list provisioned employees.")
    .Produces<IReadOnlyList<HVO.AgentControl.Organization.HireRequestSummary>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/hire-requests/{id}", (AcpControlHost host, string id) =>
{
    if (!Program.IsValidHireRequestId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid hire request id.", detail: "A bounded stable hire request id is required.");
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        var request = store.GetHireRequest(id);
        return request is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Hire request not found.")
            : Results.Ok(request);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Hire request unavailable.");
    }
})
    .WithName("GetHireRequest").WithTags("Hiring")
    .Produces<HVO.AgentControl.Organization.HireRequestSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/hire-requests", (HttpContext context, AcpControlHost host, HVO.AgentControl.Organization.HireRequestCreate request) =>
{
    if (Program.RejectCrossOrigin(context, "Hire requests") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    var headerIdempotencyKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var values)
        && !string.IsNullOrEmpty(values.ToString())
        ? values.ToString()
        : null;
    try { return Results.Ok(store.CreateHireRequest(request, headerIdempotencyKey)); }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Hire request idempotency conflict.", detail: "The idempotency key is already bound to a different immutable request payload.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Hire request unavailable.");
    }
})
    .WithName("CreateHireRequest").WithTags("Hiring")
    .WithSummary("Creates one durable owner request. It does not approve, provision, orient, or create an employee.")
    .Produces<HVO.AgentControl.Organization.HireRequestSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/hire-requests/{id}/reject", (HttpContext context, AcpControlHost host, string id, HVO.AgentControl.Organization.HireRequestReject request) =>
{
    if (!Program.IsValidHireRequestId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid hire request id.", detail: "A bounded stable hire request id is required.");
    if (Program.RejectCrossOrigin(context, "Hire request rejection") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(store.RejectHireRequest(id, request.ExpectedRevision)); }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request rejection.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Hire request not found.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Hire request rejection conflicted.", detail: "The request changed or is no longer in Requested state.");
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Hire request unavailable.");
    }
})
    .WithName("RejectHireRequest").WithTags("Hiring")
    .Produces<HVO.AgentControl.Organization.HireRequestSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Owner approval is the durable freeze of one hire against one verified profile
// build. It creates the managed employee and runtime binding from that freeze but
// never provisions or orients a worker: provisioning resumes separately and the
// hire stays Approved until that later slice moves it. Worker control must be
// enabled with a usable configuration because the frozen selection is only
// meaningful when the controller can actually consume it, and the requested
// resources must sit inside the controller-wide ceilings.
app.MapPost("/api/hire-requests/{id}/approve", (HttpContext context, AcpControlHost host, Microsoft.Extensions.Options.IOptions<HVO.AgentControl.RemoteWorker.WorkerControlOptions> options, string id, HVO.AgentControl.Organization.HireRequestApprove request) =>
{
    if (!Program.IsValidHireRequestId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid hire request id.", detail: "A bounded stable hire request id is required.");
    if (Program.RejectCrossOrigin(context, "Hire request approval") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    var workerOptions = options.Value;
    if (!workerOptions.Enabled)
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Remote worker control is disabled.", detail: "Worker control is switched off, so no approval was recorded.");
    if (workerOptions.Validate().Count != 0)
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Remote worker configuration is invalid.", detail: "Worker control is enabled but its configuration is not usable, so no approval was recorded.");
    try
    {
        var current = store.GetHireRequest(id);
        if (current is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Hire request not found.");
        if (current.CpuLimit > workerOptions.CpuLimit)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request approval.", detail: "The requested CPU limit exceeds the controller ceiling.");
        if ((long)current.MemoryLimitMiB * 1024 * 1024 > workerOptions.MemoryBytes)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request approval.", detail: "The requested memory limit exceeds the controller ceiling.");
        if (current.PidsLimit > workerOptions.PidsLimit)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request approval.", detail: "The requested PID limit exceeds the controller ceiling.");
        // The approval identity is host-derived, never taken from the request
        // body, so a caller cannot assert an arbitrary owner identity.
        var approved = store.ApproveHireRequest(id, request, Program.HireApprovalIdentity);
        store.CreateManagedEmployeeFromHire(approved.Id);
        return Results.Ok(store.GetHireRequest(approved.Id) ?? approved);
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid hire request approval.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Hire request not found.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Hire request approval conflicted.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Hire request unavailable.");
    }
})
    .WithName("ApproveHireRequest").WithTags("Hiring")
    .WithSummary("Records the durable owner approval and creates the managed employee/binding for one DeveloperContainer hire against a verified profile build. It does not provision or orient the worker; provisioning resumes separately and the request remains Approved.")
    .Produces<HVO.AgentControl.Organization.HireRequestSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/profiles", (AcpControlHost host) =>
{
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(store.ListContainerProfiles()); }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profiles unavailable.", detail: "The authoritative store could not be read.");
    }
})
    .WithName("ListContainerProfiles").WithTags("Profiles")
    .WithSummary("Lists container profiles with their current immutable revision. Profiles are build templates; listing them does not build or provision anything.")
    .Produces<IReadOnlyList<HVO.AgentControl.Organization.ContainerProfileSummary>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/profiles/{id}", (AcpControlHost host, string id) =>
{
    if (!Program.IsValidContainerProfileId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile id.", detail: "A bounded stable container profile id is required.");
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        var profile = store.GetContainerProfile(id);
        return profile is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile not found.")
            : Results.Ok(profile);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profile unavailable.");
    }
})
    .WithName("GetContainerProfile").WithTags("Profiles")
    .WithSummary("Returns one container profile with its full immutable revision chain, newest first.")
    .Produces<HVO.AgentControl.Organization.ContainerProfileDetail>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/profiles/{id}/revisions", (AcpControlHost host, string id) =>
{
    if (!Program.IsValidContainerProfileId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile id.", detail: "A bounded stable container profile id is required.");
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        var revisions = store.ListContainerProfileRevisions(id);
        return revisions is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile not found.")
            : Results.Ok(revisions);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profile revisions unavailable.");
    }
})
    .WithName("ListContainerProfileRevisions").WithTags("Profiles")
    .WithSummary("Returns the immutable revision chain of one profile, newest first. Revisions are never edited or deleted.")
    .Produces<IReadOnlyList<HVO.AgentControl.Organization.ContainerProfileRevisionSummary>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/profiles", (HttpContext context, AcpControlHost host, HVO.AgentControl.Organization.ContainerProfileCreate request) =>
{
    if (Program.RejectCrossOrigin(context, "Container profile creation") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    var headerIdempotencyKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var values)
        && !string.IsNullOrEmpty(values.ToString())
        ? values.ToString()
        : null;
    try { return Results.Ok(store.CreateContainerProfile(request, headerIdempotencyKey)); }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid container profile.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Container profile conflict.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profile unavailable.");
    }
})
    .WithName("CreateContainerProfile").WithTags("Profiles")
    .WithSummary("Creates a profile with its immutable first revision from a validated devcontainer subset. It does not build an image, approve a hire, or create an employee.")
    .Produces<HVO.AgentControl.Organization.ContainerProfileSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/profiles/{id}/revisions", (HttpContext context, AcpControlHost host, string id, HVO.AgentControl.Organization.ContainerProfileRevisionCreate request) =>
{
    if (!Program.IsValidContainerProfileId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile id.", detail: "A bounded stable container profile id is required.");
    if (Program.RejectCrossOrigin(context, "Container profile revision") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(store.CreateContainerProfileRevision(id, request)); }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid container profile revision.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile not found.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Container profile revision conflicted.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profile unavailable.");
    }
})
    .WithName("CreateContainerProfileRevision").WithTags("Profiles")
    .WithSummary("Appends the next immutable revision. Existing revisions and employees built from them are never changed.")
    .Produces<HVO.AgentControl.Organization.ContainerProfileRevisionSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/profiles/{id}/retire", (HttpContext context, AcpControlHost host, string id, HVO.AgentControl.Organization.ContainerProfileRetire request) =>
{
    if (!Program.IsValidContainerProfileId(id))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile id.", detail: "A bounded stable container profile id is required.");
    if (Program.RejectCrossOrigin(context, "Container profile retirement") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try { return Results.Ok(store.RetireContainerProfile(id, request.ExpectedRevision)); }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid container profile retirement.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile not found.");
    }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Container profile retirement conflicted.", detail: exception.Message);
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Container profile unavailable.");
    }
})
    .WithName("RetireContainerProfile").WithTags("Profiles")
    .WithSummary("Retires a profile so it cannot receive new revisions or be selected for new approvals. Existing revisions remain readable.")
    .Produces<HVO.AgentControl.Organization.ContainerProfileSummary>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/profiles/{id}/revisions/{revisionId}/builds", (AcpControlHost host, string id, string revisionId) =>
{
    if (!Program.IsValidContainerProfileId(id) || !Program.IsValidContainerProfileRevisionId(revisionId))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile or revision id.");
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        var revisions = store.ListContainerProfileRevisions(id);
        if (revisions is null || revisions.All(r => r.Id != revisionId)) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile revision not found.");
        return Results.Ok(store.ListProfileBuilds(revisionId));
    }
    catch (HVO.AgentControl.Organization.OrganizationStoreException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Profile builds unavailable."); }
})
    .WithName("ListProfileBuilds").WithTags("Profiles")
    .WithSummary("Lists the per-host image builds of one immutable profile revision, newest first.")
    .Produces<IReadOnlyList<HVO.AgentControl.Organization.ProfileBuildRecord>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/profiles/{id}/revisions/{revisionId}/builds", async (HttpContext context, AcpControlHost host, HVO.AgentControl.RemoteWorker.ProfileBuildCoordinator builds, string id, string revisionId, HVO.AgentControl.Organization.ProfileBuildRequest request) =>
{
    if (!Program.IsValidContainerProfileId(id) || !Program.IsValidContainerProfileRevisionId(revisionId))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid container profile or revision id.");
    if (Program.RejectCrossOrigin(context, "Profile image build") is { } rejection) return rejection;
    if (host.Organization is not { } store) return Program.WorkerStoreUnavailable();
    try
    {
        var revisions = store.ListContainerProfileRevisions(id);
        if (revisions is null || revisions.All(r => r.Id != revisionId)) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile revision not found.");
        // Queue returns the verified build if one exists, else the live row (new,
        // uncertain or interrupted); running a live row resumes or reconciles it.
        var queued = builds.Queue(revisionId, request.HostId);
        var result = HVO.AgentControl.Organization.ProfileBuildStates.IsLive(queued.State)
            ? await builds.RunAsync(queued.Id, context.RequestAborted)
            : queued;
        return Results.Ok(result);
    }
    catch (HVO.AgentControl.Organization.OrganizationValidationException exception) { return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid profile build.", detail: exception.Message); }
    catch (HVO.AgentControl.Organization.OrganizationNotFoundException) { return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Container profile revision not found."); }
    catch (HVO.AgentControl.Organization.OrganizationConcurrencyException exception) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Profile build conflicted.", detail: exception.Message); }
    catch (Exception exception) when (Program.IsRemoteWorkerFailure(exception)) { return Program.RemoteWorkerProblem(exception); }
})
    .WithName("BuildProfileRevision").WithTags("Profiles")
    .WithSummary("Builds and verifies the revision's image on one approved, ready execution host and records the result; called again for an uncertain or interrupted build it reconciles by tag instead of rebuilding. A verified digest joins that host's approved set; nothing is provisioned.")
    .Produces<HVO.AgentControl.Organization.ProfileBuildRecord>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
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

app.Map("/terminal", async (HttpContext context, AcpControlHost host, IRemoteWorkerStatusProvider remoteWorkers, IRemoteTerminalRouter remoteTerminal) =>
{
    var employeeId = context.Request.Query["employeeId"].ToString();
    if (!Program.IsValidEmployeeId(employeeId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Read the authoritative overview only when the store and host identity are
    // both available; a missing host identity still fails closed as 503 without
    // turning an unknown id into a 404.
    var store = host.Organization;
    var identity = host.OrganizationIdentity;
    HVO.AgentControl.Organization.OrganizationOverview? overview = null;
    if (store is not null && identity is not null)
    {
        try
        {
            overview = store.GetOverview();
        }
        catch (HVO.AgentControl.Organization.OrganizationStoreException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
    }

    IReadOnlyDictionary<string, RemoteWorkerSnapshot>? remote = null;
    if (overview is not null)
    {
        try { remote = remoteWorkers.Snapshot(overview); }
        catch (HVO.AgentControl.Organization.OrganizationStoreException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
    }
    var target = HVO.AgentControl.Organization.TerminalAttachmentResolver.Resolve(
        overview,
        identity,
        host.GetStatus(),
        remote,
        remoteTerminal.IsAvailable,
        employeeId);
    if (target.Route == HVO.AgentControl.Organization.TerminalAttachmentRoute.EmployeeNotFound)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (target.Route is HVO.AgentControl.Organization.TerminalAttachmentRoute.Unavailable or HVO.AgentControl.Organization.TerminalAttachmentRoute.RemoteUnavailable)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    if (target.Route == HVO.AgentControl.Organization.TerminalAttachmentRoute.RemoteEligible)
    {
        await remoteTerminal.ProxyAsync(context, target.Remote!, context.RequestAborted);
        return;
    }
    await TerminalEndpoint.HandleAsync(context, Path.Combine(host.DataDirectory, "home"), host.TmuxSessionName, host.AgentLauncher);
})
    .WithName("AttachTerminal")
    .WithTags("Terminal")
    .WithSummary("Attaches a single owner viewer to the owned tmux session.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Process liveness only; deliberately unauthenticated and dependency-free.
app.MapMethods("/health/live", [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
    HttpMethods.IsHead(context.Request.Method)
        ? Results.Ok()
        : Results.Ok(new HealthResponse("healthy")))
    .WithName("HealthLive")
    .WithTags("Health")
    .WithSummary("Process liveness. Does not probe the runtime or any dependency.")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

// Readiness of the exact owned ACP session and attached TUI. It is not a
// database, worker, or provider check and must not claim one.
app.MapMethods("/health/ready", [HttpMethods.Get, HttpMethods.Head], (HttpContext context, AcpControlHost host) =>
{
    var status = host.GetStatus();
    return IsRuntimeReady(status)
        ? HttpMethods.IsHead(context.Request.Method) ? Results.Ok() : Results.Ok(new HealthResponse("ready"))
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

machineContractRoutes.Initialize(((IEndpointRouteBuilder)app).DataSources);
app.Run();

public partial class Program
{
    /// <summary>Minimum length required of a configured owner password, after trimming.</summary>
    public const int MinimumOwnerPasswordLength = 24;

    /// <summary>Maximum length accepted for a stable employee id on the wire.</summary>
    public const int MaximumEmployeeIdLength = 64;

    /// <summary>Maximum length accepted for a stable department id on the wire.</summary>
    public const int MaximumDepartmentIdLength = 64;

    /// <summary>Maximum length accepted for a stable hire request id on the wire.</summary>
    public const int MaximumHireRequestIdLength = 64;
    public const int MaximumContainerProfileIdLength = 64;

    /// <summary>
    /// Fixed owner-approval identity recorded with every owner approval. It is
    /// host-derived and deliberately not taken from the request body, so a caller
    /// cannot assert an arbitrary owner identity into the immutable approval.
    /// </summary>
    public const string HireApprovalIdentity = "owner-basic-auth";

    /// <summary>
    /// Exact, stable scope label reported by <c>/api/info</c> for the worker-control
    /// capability that <see cref="InfoResponse.WorkerControlImplemented"/> and
    /// <see cref="InfoResponse.WorkerControlOperationallyValidated"/> describe. It
    /// names only the first managed disposable two-host path; it does not include
    /// key rotation/compromise re-enrollment or production managed
    /// hiring/provisioning.
    /// </summary>
    public const string WorkerControlValidatedScope = "first-managed-disposable-two-host";

    /// <summary>
    /// Returns true for paths whose HTTP error contract is always RFC 9457 JSON
    /// rather than the portal's friendly HTML. Segment-aware prefix matching
    /// keeps unrelated paths such as <c>/apiary</c> in the portal namespace.
    /// </summary>
    public static bool IsMachineContractPath(PathString path) =>
        path.StartsWithSegments("/api")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/openapi")
        || path.StartsWithSegments("/terminal");

    /// <summary>Returns true for an OpenAPI document path other than the configured v1 document.</summary>
    internal static bool IsUnknownOpenApiPath(PathString path) =>
        path.StartsWithSegments("/openapi")
        && !path.Equals("/openapi/v1.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true for a concrete owner-portal route whose implemented method is GET.</summary>
    internal static bool IsKnownPortalGetPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/') ?? string.Empty;
        if (value.Length == 0)
        {
            value = "/";
        }

        if (value is "/" or "/organization" or "/organization/departments" or "/employees" or "/hiring" or "/profiles" or "/system")
        {
            return true;
        }

        return HasSingleRouteValue(value, "/organization/departments/")
            || HasSingleRouteValue(value, "/employees/")
            || HasSingleRouteValue(value, "/profiles/");
    }

    private static bool HasSingleRouteValue(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && path.Length > prefix.Length
        && path.IndexOf('/', prefix.Length) < 0;

    /// <summary>Identifies the Razor component endpoint reserved for portal 404 HTML.</summary>
    internal static bool IsPortalCatchAll(Endpoint? endpoint) =>
        endpoint is RouteEndpoint route
        && route.RoutePattern.Parameters.Any(parameter => parameter.IsCatchAll);

    /// <summary>Identifies endpoint routing's synthetic wrong-method endpoint.</summary>
    internal static bool IsFrameworkMethodNotAllowed(Endpoint? endpoint) =>
        string.Equals(endpoint?.DisplayName, "405 HTTP Method Not Supported", StringComparison.Ordinal);

    /// <summary>Writes the required ProblemDetails media type regardless of Accept.</summary>
    internal static async Task WriteProblemAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        if (await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = statusCode },
        }))
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Type = ProblemType(statusCode),
            Instance = context.Request.Path,
        };
        problem.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }

    /// <summary>
    /// Cached endpoint metadata used only when endpoint routing selected the
    /// Razor portal catch-all for a machine-contract path. It recovers the same
    /// 404/405/415 distinction without scanning endpoints on normal requests.
    /// </summary>
    internal sealed class MachineContractRouteCatalog
    {
        private readonly List<MachineContractRoute> _routes = [];

        public void Initialize(IEnumerable<EndpointDataSource> dataSources)
        {
            _routes.AddRange(dataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(route => !route.RoutePattern.Parameters.Any(parameter => parameter.IsCatchAll))
                .Select(route =>
                {
                    var accepts = route.Metadata.GetMetadata<IAcceptsMetadata>();
                    return new MachineContractRoute(
                        new TemplateMatcher(
                            TemplateParser.Parse(route.RoutePattern.RawText ?? string.Empty),
                            new RouteValueDictionary()),
                        route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                            .Select(method => method.ToUpperInvariant())
                            .ToArray() ?? [],
                        accepts?.ContentTypes.ToArray() ?? [],
                        accepts is not null,
                        accepts?.RequestType is not null && !accepts.IsOptional);
                }));
        }

        public MachineContractRejection? Classify(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var pathMatches = _routes
                .Where(route => route.Matcher.TryMatch(path, new RouteValueDictionary()))
                .ToArray();
            if (pathMatches.Length == 0)
            {
                return new(StatusCodes.Status404NotFound, []);
            }

            var method = context.Request.Method.ToUpperInvariant();
            var methodMatches = pathMatches
                .Where(route => route.Methods.Count == 0
                    || route.Methods.Contains(method, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (methodMatches.Length == 0)
            {
                var allowed = pathMatches
                    .SelectMany(route => route.Methods)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new(StatusCodes.Status405MethodNotAllowed, allowed);
            }

            var acceptsMatches = methodMatches.Where(route => route.HasAcceptsMetadata).ToArray();
            if (acceptsMatches.Length > 0)
            {
                var contentTypePresent = !string.IsNullOrWhiteSpace(context.Request.ContentType);
                var contentTypeAccepted = contentTypePresent
                    && acceptsMatches.Any(route => route.ContentTypes.Count == 0
                        || route.ContentTypes.Any(contentType => MediaTypeHeaderValue.TryParse(contentType, out var accepted)
                            && MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var supplied)
                            && supplied.IsSubsetOf(accepted)));

                // A supplied media type is authoritative even when a server or
                // test transport normalizes away Content-Length and
                // Transfer-Encoding. Wrong Content-Type is therefore always 415,
                // including an explicitly empty body.
                if (contentTypePresent && !contentTypeAccepted)
                {
                    return new(StatusCodes.Status415UnsupportedMediaType, []);
                }

                var hasBodySignal = context.Request.ContentLength is > 0
                    || context.Request.Headers.TransferEncoding.Count > 0;
                if (!contentTypePresent && hasBodySignal)
                {
                    return new(StatusCodes.Status415UnsupportedMediaType, []);
                }

                if (!contentTypePresent
                    && !hasBodySignal
                    && acceptsMatches.All(route => route.BodyRequired))
                {
                    return new(StatusCodes.Status400BadRequest, []);
                }
            }

            // The path and method are known and content type is acceptable. This
            // should normally have selected the real endpoint, so fail closed as
            // not found rather than execute portal HTML for a machine path.
            return new(StatusCodes.Status404NotFound, []);
        }

        private sealed record MachineContractRoute(
            TemplateMatcher Matcher,
            IReadOnlyList<string> Methods,
            IReadOnlyList<string> ContentTypes,
            bool HasAcceptsMetadata,
            bool BodyRequired);
    }

    internal sealed record MachineContractRejection(int StatusCode, IReadOnlyList<string> AllowedMethods);

    private static string ProblemType(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
        StatusCodes.Status401Unauthorized => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2",
        StatusCodes.Status403Forbidden => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
        StatusCodes.Status404NotFound => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.5",
        StatusCodes.Status405MethodNotAllowed => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.6",
        StatusCodes.Status415UnsupportedMediaType => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.16",
        StatusCodes.Status500InternalServerError => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
        StatusCodes.Status503ServiceUnavailable => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.4",
        _ => "about:blank",
    };

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
    /// True when <paramref name="exception"/> is one the remote-worker endpoints
    /// translate into the single RFC 9457 error contract. Used as an exception
    /// filter so unexpected failures still reach the sanitized 500 handler.
    /// </summary>
    public static bool IsRemoteWorkerFailure(Exception exception) =>
        exception is HVO.AgentControl.Organization.OrganizationStoreException
            or HVO.AgentControl.RemoteWorker.RemoteWorkerException
            or HVO.AgentControl.RemoteWorker.WorkerWriteUncertainException
            or HVO.AgentControl.RemoteWorker.WorkerReadUncertainException
            or HVO.AgentControl.RemoteWorker.WorkerRemoteException
            or HVO.AgentControl.RemoteWorker.WorkerReconciliationInvalidException
            or HVO.AgentControl.Worker.WorkerOperationUncertainException
            or KeyNotFoundException
            or InvalidOperationException;

    /// <summary>
    /// Maps a typed remote-worker failure to the single RFC 9457 error contract.
    /// </summary>
    /// <remarks>
    /// Each arm is a distinct operator meaning, so no caller has to guess intent
    /// from a bare <see cref="InvalidOperationException"/>. A disabled or
    /// misconfigured controller is 409; an unreachable host transport is 502 and a
    /// reachable-but-unusable host is 503; an open recovery obligation is a 409
    /// that names the obligation kind; a foreign resource is 409. The remaining
    /// <see cref="InvalidOperationException"/> arm is a sanitized 500: reaching it
    /// means an internal invariant failed rather than a caller or host condition,
    /// and it must not be reported as a client-fixable conflict.
    /// </remarks>
    public static IResult RemoteWorkerProblem(Exception exception) => exception switch
    {
        HVO.AgentControl.Organization.OrganizationValidationException => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Remote worker request is invalid."),
        HVO.AgentControl.RemoteWorker.WorkerRecoveryRequiredException recovery => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote worker recovery is required.",
            detail: $"An operator must reconcile the open '{recovery.Kind}' obligation before this operation can proceed."),
        HVO.AgentControl.RemoteWorker.ForeignResourceException => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote resource is not owned by this controller.",
            detail: "A resource with the expected name exists but does not carry this controller's exact labels."),
        HVO.AgentControl.RemoteWorker.WorkerPermissionOptionsUnsupportedException => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Worker permission cannot be rejected safely.",
            detail: "The worker offered no recognized reject option; dispatch remains held. Update compatibility before retrying."),
        HVO.AgentControl.Organization.OrganizationConcurrencyException => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote worker request conflicted.",
            detail: "The referenced worker state changed. Reload and retry."),
        HVO.AgentControl.Organization.OrganizationNotFoundException => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Remote worker record not found."),
        KeyNotFoundException => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Remote worker record not found."),
        HVO.AgentControl.RemoteWorker.WorkerControlDisabledException => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote worker control is disabled.",
            detail: "Worker control is switched off, so no host operation was executed."),
        HVO.AgentControl.RemoteWorker.WorkerControlConfigurationException => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote worker configuration is invalid.",
            detail: "Worker control is enabled but its configuration is not usable, so no host operation was executed."),
        HVO.AgentControl.RemoteWorker.RemoteWorkerUnavailableException unavailable => Results.Problem(
            statusCode: unavailable.Transport ? StatusCodes.Status502BadGateway : StatusCodes.Status503ServiceUnavailable,
            title: unavailable.Transport ? "Remote worker host is unreachable." : "Remote worker host is unavailable.",
            detail: unavailable.Transport
                ? "The fixed connector could not reach the approved host."
                : "The approved host did not return a usable result."),
        HVO.AgentControl.RemoteWorker.WorkerReconciliationInvalidException => Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Remote worker reconciliation is invalid",
            detail: "Worker returned state that could not be correlated; dispatch remains held."),
        HVO.AgentControl.RemoteWorker.WorkerRemoteException { Code: "worker-request-rejected" } => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Remote worker bridge rejected the operation.",
            detail: "The worker definitely rejected the request."),
        HVO.AgentControl.RemoteWorker.WorkerWriteUncertainException or HVO.AgentControl.RemoteWorker.WorkerReadUncertainException or HVO.AgentControl.Worker.WorkerOperationUncertainException or HVO.AgentControl.RemoteWorker.WorkerRemoteException => Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Remote worker bridge is unavailable.",
            detail: "The bridge transport failed or the remote operation outcome is uncertain."),
        HVO.AgentControl.Organization.OrganizationStoreException => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Worker store unavailable.",
            detail: "The authoritative store could not be read or written."),
        InvalidOperationException => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Remote worker operation failed.",
            detail: "An internal invariant failed. The operation was not completed."),
        _ => throw exception,
    };

    /// <summary>The control runtime is disabled or its authoritative store is not open.</summary>
    public static IResult WorkerStoreUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Worker store unavailable.",
        detail: "The control runtime is disabled or the authoritative store has not opened.");

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
    /// Validates the stable employee-id wire shape before any store or terminal
    /// work. A valid id carries the <c>emp-</c> prefix, at least one lower-case
    /// alphanumeric suffix character, only ASCII lower-case letters, digits and
    /// internal hyphens, and a bounded total length. A bare prefix or a suffix
    /// made only of hyphens is rejected. Generated hex ids and persisted test
    /// ids such as <c>emp-test</c> both fit this shape.
    /// </summary>
    public static bool IsValidEmployeeId(string? value) =>
        IsValidStableId(value, HVO.AgentControl.Organization.OrganizationIds.EmployeePrefix, MaximumEmployeeIdLength);

    /// <summary>
    /// Validates the stable department-id wire shape accepted by
    /// <c>/api/departments/{id}</c>. It applies the same bounded, lower-case,
    /// prefix-bound rule as <see cref="IsValidEmployeeId"/> so a path traversal,
    /// uppercase, padded or forged value is rejected before any store read.
    /// </summary>
    public static bool IsValidDepartmentId(string? value) =>
        IsValidStableId(value, HVO.AgentControl.Organization.OrganizationIds.DepartmentPrefix, MaximumDepartmentIdLength);

    /// <summary>Validates the bounded stable ID used by hire request read/reject routes.</summary>
    public static bool IsValidHireRequestId(string? value) =>
        IsValidStableId(value, HVO.AgentControl.Organization.OrganizationIds.HireRequestPrefix, MaximumHireRequestIdLength);

    /// <summary>Validates the bounded stable ID used by container profile routes.</summary>
    public static bool IsValidContainerProfileId(string? value) =>
        IsValidStableId(value, HVO.AgentControl.Organization.OrganizationIds.ContainerProfilePrefix, MaximumContainerProfileIdLength);

    public static bool IsValidContainerProfileRevisionId(string? value) =>
        IsValidStableId(value, HVO.AgentControl.Organization.OrganizationIds.ContainerProfileRevisionPrefix, MaximumContainerProfileIdLength);

    private static bool IsValidStableId(string? value, string prefix, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            return false;
        }

        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = value.AsSpan(prefix.Length);
        if (suffix.IsEmpty
            || suffix.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-".AsSpan()) >= 0)
        {
            return false;
        }

        // A suffix of only hyphens is not a usable stable id: it must contain at
        // least one lower-case letter or digit after the prefix.
        return suffix.IndexOfAnyInRange('a', 'z') >= 0
            || suffix.IndexOfAnyInRange('0', '9') >= 0;
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

public sealed record WorkerEnrollPlanRequest(string RuntimeBindingId, string HostId, string? ImageDigest = null);
public sealed record WorkerPromptRequest(string EmployeeId, string RuntimeBindingId, string WorkerId, string SessionRecordId, string NativeSessionId, string IdempotencyKey, string Prompt);
public sealed record WorkerRecoveryRequest(string ObligationId, int ExpectedRevision);
public sealed record WorkerRecoveryAcknowledgementRequest(int ExpectedRevision, string EvidenceHash, string Disposition);
public sealed record WorkerPermissionRejectRequest(string DecisionId, int Revision);
public sealed record ModelSelection(string? Model);

public sealed record OrganizationUpdate(string? OrganizationId, string? DisplayName, int? Revision);
public sealed record OrganizationInstructionsUpdate(string? OrganizationId, string? BasicInstructions, int? Revision);
public sealed record RoleInstructionsUpdate(string? StandingInstructions, int? Revision);
public sealed record ManualHoldUpdate(bool Held, string? Detail);
public sealed record GrantRevokeRequest(int ExpectedRevision);

public sealed record InfoResponse(string Name, int Generation, string Status, bool WorkerControlImplemented, bool WorkerControlCodeAvailable, bool WorkerControlEnabled, bool WorkerControlOperationallyValidated, string WorkerControlValidatedScope);

public sealed record ModelResponse(string Model);

public sealed record CancelResponse(string Status, bool Completed);

public sealed record HealthResponse(string Status);

public sealed record VersionResponse(string Version, string AgentHarness, string ControlProtocol, string ExecutionEnvironment);
