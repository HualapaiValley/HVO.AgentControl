using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HVO.AgentControl.Components;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Control").Get<ControlOptions>() ?? new ControlOptions();
settings.DataDirectory = Path.GetFullPath(settings.DataDirectory);
settings.SecretsDirectory = Path.GetFullPath(settings.SecretsDirectory);
Directory.CreateDirectory(settings.DataDirectory);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(settings.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
if (settings.GlobalCapacity is < 1 or > 128 || settings.QueueLimit is < 1 or > 256 || settings.EventRetention is < 100 or > 1000000 ||
    settings.HistoryLimit is < 10 or > 1000 || settings.PollMilliseconds is < 100 or > 2000 || settings.MaxPromptCharacters is < 1 or > 64000 ||
    settings.MaxRuntimes is < 1 or > 128 || settings.MaxWorkers is < 1 or > 1024 || settings.MaxCommandRecords is < 10 or > 1000000)
    throw new InvalidOperationException("Control limits are outside supported bounds; inspect Control configuration.");
builder.Services.Configure<ControlOptions>(options =>
{
    builder.Configuration.GetSection("Control").Bind(options);
    options.DataDirectory = settings.DataDirectory; options.SecretsDirectory = settings.SecretsDirectory;
});
builder.Services.AddSingleton(_ => new ReplicaLock(settings.DataDirectory));
builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton(_ => new HVO.AgentControl.GitHub.GitHubAppClient(
    new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) }, TimeProvider.System));
builder.Services.AddSingleton<HVO.AgentControl.GitHub.GitHubAccessService>();
builder.Services.AddSingleton<HVO.AgentControl.GitHub.GitHubCredentialDelivery>();
builder.Services.AddHostedService<HVO.AgentControl.GitHub.GitHubCredentialSupervisor>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys"))).SetApplicationName("HVO.AgentControl");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/login";
    options.Cookie.Name = "HvoAgentControl"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = settings.AllowInsecureLocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(12); options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in settings.TrustedProxies) options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, OwnerAuthenticationStateProvider>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddDbContextFactory<ControlDb>(options => options.UseSqlite($"Data Source={Path.Combine(settings.DataDirectory, "agentcontrol.db")};Default Timeout=10"));
builder.Services.AddSingleton<ControlStore>();
builder.Services.AddSingleton<RuntimeVerificationService>();
builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<IRuntimeTransportFactory, SshRuntimeTransportFactory>();
builder.Services.AddHostedService<RuntimeSupervisor>();
builder.Services.AddHostedService<CoordinatorService>();
builder.Services.AddHostedService<ActivityPublisher>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.MaxBufferedUnacknowledgedRenderBatches = 5)
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
var app = builder.Build();
if (settings.TrustedProxies.Length > 0) app.UseForwardedHeaders();
_ = app.Services.GetRequiredService<ReplicaLock>();
var ownerSecret = app.Services.GetRequiredService<Secrets>().Read(settings.OwnerPasswordFile);
if (ownerSecret.Length < 24) throw new InvalidOperationException("The mounted owner-password secret must contain at least 24 characters.");
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ControlDb>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/") context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (ControlException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid antiforgery token; reload the page." }); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogError("Request failed ({Category}); no durable acceptance was returned", ex.GetType().Name);
        context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Operation failed. Retrieve the application request ID before retrying; inspect service diagnostics." });
    }
});
if (!settings.AllowInsecureLocalHttp) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseWebSockets();
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var supplied = form["password"].ToString();
    var expected = app.Services.GetRequiredService<Secrets>().Read(settings.OwnerPasswordFile);
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
        return Results.Redirect("/login?failed=true");
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner"), new Claim("hvo:expires", DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect("/");
}).RequireRateLimiting("login");
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context); await context.SignOutAsync(); return Results.Redirect("/login");
}).RequireAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (IDbContextFactory<ControlDb> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    return await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", scope = "control-plane database" }) : Results.StatusCode(503);
});
app.MapGet("/api/v1/runtimes/{id}/terminal", (HttpContext context, string id, TerminalService terminal, IAntiforgery antiforgery) => terminal.Connect(context, id, antiforgery)).RequireAuthorization();
app.MapControlApi();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();

public partial class Program;
