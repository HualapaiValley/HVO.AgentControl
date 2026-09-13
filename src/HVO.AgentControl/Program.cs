using HVO.AgentControl.Components;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddAcpControlHost(builder.Configuration);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

var passwordFile = builder.Configuration["Control:OwnerPasswordFile"];
var ownerPassword = string.IsNullOrEmpty(passwordFile) ? null : File.ReadAllText(passwordFile).Trim();
if (builder.Configuration.GetValue<bool>("Control:Enabled") && (ownerPassword?.Length ?? 0) < 24)
{
    throw new InvalidOperationException("An owner password file containing at least 24 characters is required when the runtime is enabled.");
}
var app = builder.Build();

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
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"AgentControl\", charset=\"UTF-8\"";
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

app.MapGet("/api/info", () => Results.Ok(new
{
    name = "HVO.AgentControl",
    generation = 2,
    status = "control-portal",
    workerControlImplemented = false
}));

app.MapGet("/api/control", (AcpControlHost host) => Results.Ok(host.GetStatus()));
app.MapPost("/api/control/model", async (HttpContext context, AcpControlHost host, ModelSelection selection) =>
{
    if (!TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(),
            context.Request.Scheme, context.Request.Host.Value))
        return Results.StatusCode(403);
    if (host.GetStatus().State != "ready") return Results.StatusCode(409);
    if (string.IsNullOrWhiteSpace(selection.Model) || selection.Model.Length > 256)
        return Results.BadRequest(new { error = "Select an advertised provider/model." });
    if (!host.GetStatus().ModelSyncSupported)
        return Results.Conflict(new { error = "The attached OpenCode TUI does not support synchronized model selection yet. Use the TUI picker; the displayed server model updates after submission." });
    try
    {
        if (!await host.SetModelAsync(selection.Model, context.RequestAborted))
            return Results.Json(new { error = "Model change could not be confirmed. Check the session before retrying." }, statusCode: 502);
        return Results.Ok(new { model = host.GetStatus().Model });
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "Select an advertised provider/model." });
    }
});
app.MapPost("/api/control/cancel", async (HttpContext context, AcpControlHost host) =>
{
    if (!TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(),
            context.Request.Scheme, context.Request.Host.Value))
        return Results.StatusCode(403);
    if (host.GetStatus().State != "ready") return Results.StatusCode(409);
    if (!await host.CancelAsync(context.RequestAborted)) return Results.StatusCode(503);
    return Results.Accepted(value: new { status = "cancel-requested", completed = false });
});
app.Map("/terminal", async (HttpContext context, AcpControlHost host) =>
{
    if (host.GetStatus().State != "ready" || !host.GetStatus().TerminalReady)
    {
        context.Response.StatusCode = 503;
        return;
    }
    await TerminalEndpoint.HandleAsync(context, Path.Combine(host.DataDirectory, "home"), host.TmuxSessionName);
});

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/version", () => Results.Ok(new
{
    version = HVO.AgentControl.ApplicationVersion.Current,
    agentHarness = "OpenCode",
    controlProtocol = "ACP",
    executionEnvironment = "Docker"
}));

app.Run();

public partial class Program;

public sealed record ModelSelection(string? Model);
