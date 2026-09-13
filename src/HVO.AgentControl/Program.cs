using HVO.AgentControl.Components;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddAcpControlHost(builder.Configuration);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

// A binding failure is a client 400, never a 500. Without this, Development
// rethrows BadHttpRequestException and the exception handler would mask a
// malformed request as a server fault.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var passwordFile = builder.Configuration["Control:OwnerPasswordFile"];
var ownerPassword = string.IsNullOrEmpty(passwordFile) ? null : File.ReadAllText(passwordFile).Trim();
if (builder.Configuration.GetValue<bool>("Control:Enabled") && (ownerPassword?.Length ?? 0) < 24)
{
    throw new InvalidOperationException("An owner password file containing at least 24 characters is required when the runtime is enabled.");
}
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
    if (host.GetStatus().State != "ready")
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runtime not ready.",
            detail: "The control runtime must be ready before cancellation.");
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
    if (host.GetStatus().State != "ready" || !host.GetStatus().TerminalReady)
    {
        context.Response.StatusCode = 503;
        return;
    }
    await TerminalEndpoint.HandleAsync(context, Path.Combine(host.DataDirectory, "home"), host.TmuxSessionName);
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

public sealed record InfoResponse(string Name, int Generation, string Status, bool WorkerControlImplemented);

public sealed record ModelResponse(string Model);

public sealed record CancelResponse(string Status, bool Completed);

public sealed record HealthResponse(string Status);

public sealed record VersionResponse(string Version, string AgentHarness, string ControlProtocol, string ExecutionEnvironment);
