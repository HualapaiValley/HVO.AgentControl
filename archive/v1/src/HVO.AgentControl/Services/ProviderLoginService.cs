using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Ssh;

namespace HVO.AgentControl.Services;

public sealed record ProviderLogin(string Id, string RuntimeId, string State, string Detail,
    string? Url = null, string? Code = null, long? WaitingUntil = null);

// OpenCode owns the OAuth transaction and tokens. Only browser instructions live here.
// A page refresh does not cancel authentication; host restart loses pending receipts.
public sealed class ProviderLoginService(ControlStore store, IRuntimeTransportFactory transports,
    IHostApplicationLifetime host, ILogger<ProviderLoginService> logger)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ProviderLogin> logins = [];
    private readonly HashSet<string> active = [];

    public List<ProviderLogin> List() { lock (gate) return logins.Values.ToList(); }

    public async Task<ProviderLogin> Start(string runtimeId, string requestId)
    {
        if (!Guid.TryParse(requestId, out _)) throw new ControlException("A sign-in request ID is required.", 400);
        var runtime = await store.Read(async db => await db.Runtimes.FindAsync(runtimeId))
            ?? throw new ControlException("Runtime not found.", 404);
        if (runtime.ConnectionKind == RuntimeConnections.ManagedDraft)
            throw new ControlException("Managed runtime enrollment is pending; provider sign-in is unavailable until transport ownership is verified.");
        if (!runtime.DesiredConnected || runtime.Health != "Healthy")
            throw new ControlException("Verify and connect this runtime before signing in.");
        ProviderLogin login;
        lock (gate)
        {
            if (logins.TryGetValue(runtimeId, out var prior) && (prior.Id == requestId || active.Contains(runtimeId))) return prior;
            if (active.Count >= 4) throw new ControlException("Four sign-ins are pending. Finish one before starting another.");
            // Bound retained receipts even if registrations are continually created and deleted.
            if (logins.Count >= 128 && !logins.ContainsKey(runtimeId))
                logins.Remove(logins.Keys.First(x => !active.Contains(x)));
            login = new(requestId, runtimeId, "Starting", "Connecting to OpenCode to request a device code.");
            logins[runtimeId] = login;
            active.Add(runtimeId);
        }
        _ = Run(runtime, login);
        return login;
    }

    public static int HeadlessMethod(JsonElement methods)
    {
        if (methods.TryGetProperty("openai", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var method in entries.EnumerateArray())
            {
                if (method.TryGetProperty("type", out var type) && type.GetString() == "oauth" &&
                    method.TryGetProperty("label", out var label) && label.GetString() == "ChatGPT Pro/Plus (headless)") return index;
                index++;
            }
        }
        throw new ControlException("This OpenCode runtime does not offer ChatGPT device sign-in.", 400);
    }

    public static (string Url, string Code) BrowserInstructions(JsonElement authorization)
    {
        // Never render arbitrary plugin URLs, instructions or HTML in an account-approval flow.
        if (!authorization.TryGetProperty("url", out var url) || url.GetString() != "https://auth.openai.com/codex/device" ||
            !authorization.TryGetProperty("method", out var method) || method.GetString() != "auto" ||
            !authorization.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.String)
            throw new ControlException("OpenCode returned an unsupported device authorization response.", 400);
        var match = Regex.Match(instructions.GetString()!, @"\AEnter code: ([A-Z0-9]{4}-[A-Z0-9]{5})\z", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ControlException("OpenCode returned an unsupported device code format.", 400);
        return (url.GetString()!, match.Groups[1].Value);
    }

    private async Task Run(RuntimeRecord runtime, ProviderLogin login)
    {
        var callbackStarted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var trackingUntil = ControlStore.Now + 10 * 60_000;
            await using var transport = await transports.Connect(runtime, deadline.Token);
            var directory = ControlStore.Roots(runtime)[0];
            var method = HeadlessMethod(await transport.Api.ProviderAuthMethods(directory, deadline.Token));
            var (url, code) = BrowserInstructions(await transport.Api.AuthorizeChatGpt(directory, method, deadline.Token));
            await Update(login with
            {
                State = "Waiting",
                Detail = "Open the sign-in page and approve this device with your ChatGPT account.",
                Url = url,
                Code = code,
                WaitingUntil = trackingUntil
            });
            callbackStarted = true;
            var result = await transport.Api.CompleteChatGpt(directory, method, deadline.Token);
            if (result.ValueKind != JsonValueKind.True) throw new InvalidDataException("OAuth completion was not confirmed.");
            await Update(login with { State = "Connected", Detail = "OpenCode confirmed sign-in. Credentials remain on this runtime. Worker models and running sessions are unchanged." });
        }
        catch (Exception ex)
        {
            logger.LogInformation("Provider sign-in {RequestId} ended ({Category})", login.Id, ex.GetType().Name);
            var uncertain = callbackStarted && ex is not NativeRejectedException;
            await Update(login with
            {
                State = uncertain ? "Unknown" : "Failed",
                Detail = uncertain
                ? "Sign-in completion could not be confirmed. OpenCode may still finish authorization; check the runtime before starting again."
                : "Sign-in could not be completed. Check runtime connectivity and ChatGPT device-login settings, then try again."
            });
        }
        finally { lock (gate) active.Remove(runtime.Id); }
    }

    private async Task Update(ProviderLogin login)
    {
        lock (gate) logins[login.RuntimeId] = login;
        try
        {
            await store.Write(db =>
            {
                ControlStore.Event(db, "ProviderLoginChanged", login.RuntimeId, payload: new { login.Id, login.State }, provenance: "user");
                return Task.FromResult(true);
            });
        }
        catch (Exception ex) { logger.LogInformation("Provider sign-in receipt notification failed ({Category})", ex.GetType().Name); }
    }
}
