using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using HVO.AgentControl.Telemetry;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class Runtimes
{
    [Inject] private RuntimeVerificationService Verification { get; set; } = default!;
    private IEnumerable<RuntimeRecord> DevelopmentRuntimes => (snapshot?.Runtimes ?? []).Where(x => x.ConnectionKind == RuntimeConnections.Ssh);
    private RuntimeRecord? runtimeEdit;
    private string? deletingRuntimeId;
    private RuntimeVerification? runtimeVerification;
    private string sshPassword = "", sshPrivateKey = "", sshPassphrase = "";
    private string? runtimeSetupId, runtimeConnectId;
    private Dictionary<string, List<RuntimeTelemetryHistoryRecord>> telemetryHistory = [];

    private static RuntimeTelemetryProjection? Telemetry(RuntimeRecord runtime) =>
        RuntimeTelemetryProjection.FromCapabilities(runtime.CapabilitiesJson, $"{runtime.Id}:{runtime.Generation}");

    private static string Bytes(long? value)
    {
        if (value is null) return "unknown";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)value.Value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return amount.ToString(amount >= 10 || unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    protected override async Task SnapshotChanged()
    {
        telemetryHistory = [];
        foreach (var runtime in DevelopmentRuntimes)
            telemetryHistory[runtime.Id] = await Store.TelemetryHistory(runtime.Id, 5);
        if (runtimeSetupId is not null && snapshot!.Runtimes.Any(x => x.Id == runtimeSetupId && x.Health == "Healthy"))
        {
            var ready = runtimeSetupId; runtimeSetupId = null;
            Navigation.NavigateTo(WorkerSetupUrl(ready));
        }
    }
    private void NewRuntime() { CancelRuntime(); runtimeEdit = new RuntimeRecord { Authentication = "password" }; }
    private void EditRuntime(RuntimeRecord runtime) { CancelRuntime(); runtimeEdit = Json.Read<RuntimeRecord>(Json.Write(runtime)); }
    private void ClearVerification() { runtimeVerification = null; runtimeConnectId = null; }
    private void AuthenticationChanged() { runtimeEdit!.CredentialReference = ""; runtimeEdit.PassphraseReference = null; sshPassword = ""; sshPrivateKey = ""; sshPassphrase = ""; ClearVerification(); }
    private void CancelRuntime() { runtimeEdit = null; ClearVerification(); sshPassword = ""; sshPrivateKey = ""; sshPassphrase = ""; }
    private Task VerifyRuntime(bool trust = false) => Execute(async () =>
    {
        var result = await Verification.Verify(new(runtimeEdit!, sshPassword, sshPrivateKey, sshPassphrase,
            trust ? runtimeVerification?.TrustToken : null), lifetime.Token);
        runtimeVerification = result;
        if (result.Status != "TrustRequired") runtimeEdit = result.Profile;
        if (result.SaveToken is not null) { sshPassword = ""; sshPrivateKey = ""; sshPassphrase = ""; }
    });
    private Task SaveForSetup() => Execute(async () =>
    {
        runtimeConnectId ??= Guid.NewGuid().ToString();
        await Verification.SaveForSetup(new(runtimeEdit!, runtimeVerification!.SaveToken!, runtimeConnectId));
        CancelRuntime(); notice = "Runtime saved without starting OpenCode. Use Open admin terminal to finish setup, then Edit and Verify again.";
    });
    private Task SaveVerifiedRuntime() => Execute(async () =>
    {
        runtimeConnectId ??= Guid.NewGuid().ToString();
        await Verification.SaveAndConnect(new(runtimeEdit!, runtimeVerification!.VerificationToken!, runtimeConnectId));
        runtimeSetupId = runtimeEdit!.Id; CancelRuntime();
        notice = "Profile saved. Connecting and checking OpenCode; worker setup will open when ready.";
    });
    private Task SaveRuntime() => Execute(async () => { await Store.SaveRuntime(runtimeEdit!); CancelRuntime(); notice = "Runtime profile saved. Connect to validate and bootstrap."; });
    private Task DeleteRuntime(RuntimeRecord runtime) => Execute(async () =>
    {
        await Store.DeleteRuntime(runtime.Id, new(Guid.NewGuid().ToString(), runtime.Revision));
        deletingRuntimeId = null; notice = "Runtime registration deleted. Remote processes, files and saved credentials retained.";
    });
    private Task RuntimeAction(string id, string kind) => Execute(async () => { var command = await Store.RuntimeCommand(id, kind, Guid.NewGuid().ToString()); notice = kind + " recorded: " + command.Id; });
}
