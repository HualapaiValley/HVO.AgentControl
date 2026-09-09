using System.Globalization;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Ssh;

public static class CapabilityProbeStatus
{
    public const string Available = "Available", Unavailable = "Unavailable", Unknown = "Unknown";
}

public sealed record CapabilityProbeDefinition(string Id, int Version, string Kind, string FactKey, string Description);
public sealed record CapabilityProbeResult(string Id, int Version, string Status, string Value);
public sealed record CapabilityProbeCatalogContract(int SchemaVersion, int CatalogVersion, IReadOnlyList<CapabilityProbeDefinition> Probes);
public sealed record WorkerSlotCapabilityReport(string WorkerSlotId, long WorkerSlotRevision, IReadOnlyList<string> RequiredProbeIds,
    IReadOnlyList<string> MissingProbeIds, bool CapabilityRequirementsSatisfied);
public sealed record RuntimeCapabilityReport(int SchemaVersion, int CatalogVersion, string RuntimeId, int RuntimeGeneration,
    long? ObservedAt, string Source, string Scope, IReadOnlyList<CapabilityProbeResult> Results,
    IReadOnlyList<WorkerSlotCapabilityReport> WorkerSlots);

public sealed class CapabilityProbeCatalog
{
    public const int SchemaVersion = 1, CatalogVersion = 1;

    private static readonly CapabilityProbeDefinition[] registered =
    [
        new("environment.os", 1, "Environment", "os", "Operating system is identified."),
        new("environment.architecture", 1, "Environment", "architecture", "Processor architecture is identified."),
        new("environment.scope", 1, "Environment", "executionScope", "Machine or container execution scope is identified."),
        new("resource.cpu", 1, "Resource", "effectiveCpuCores", "Effective CPU capacity is measurable."),
        new("resource.memory", 1, "Resource", "effectiveMemoryBytes", "Effective memory capacity is measurable."),
        new("resource.workspace-disk", 1, "Resource", "workspaceFreeKiB", "Workspace free disk is measurable."),
        new("tool.git", 1, "Tool", "tool.git", "Git executable is available."),
        new("tool.gh", 1, "Tool", "tool.gh", "GitHub CLI executable is available."),
        new("tool.dotnet", 1, "Tool", "tool.dotnet", ".NET SDK executable is available."),
        new("tool.node", 1, "Tool", "tool.node", "Node.js executable is available."),
        new("tool.npm", 1, "Tool", "tool.npm", "npm executable is available."),
        new("tool.python3", 1, "Tool", "tool.python3", "Python 3 executable is available."),
        new("tool.docker", 1, "Tool", "tool.docker", "Docker CLI executable is available."),
        new("access.docker-daemon", 1, "Access", "dockerDaemonAccess", "Docker daemon access is verified."),
        new("access.gpu", 1, "Access", "gpuUsable", "Usable GPU access is verified."),
        new("access.ios-build", 1, "Access", "iosBuildAccess", "iOS build access is verified."),
        new("access.signing", 1, "Access", "signingAccess", "Signing access is verified.")
    ];
    public static CapabilityProbeCatalog Default { get; } = new();

    private readonly Dictionary<string, CapabilityProbeDefinition> byId =
        registered.ToDictionary(x => x.Id, StringComparer.Ordinal);

    public CapabilityProbeCatalogContract Contract => new(SchemaVersion, CatalogVersion, registered);

    public string[] NormalizeRequirements(IEnumerable<string>? requested)
    {
        var ids = (requested ?? []).Select(x => x?.Trim() ?? "").Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (ids.Length > 32 || ids.Any(x => !byId.ContainsKey(x)))
            throw new ArgumentException("Capability probe IDs must name at most 32 registered catalog entries.");
        return ids;
    }

    public CapabilitySnapshot Normalize(CapabilitySnapshot snapshot)
    {
        if (snapshot.Facts is null || snapshot.SchemaVersion != SchemaVersion || snapshot.CatalogVersion is < 1 or > CatalogVersion ||
            snapshot.ObservedAt <= 0 || string.IsNullOrWhiteSpace(snapshot.Source) || string.IsNullOrWhiteSpace(snapshot.Scope))
            throw new InvalidOperationException("Unsupported capability probe schema or catalog version.");
        return snapshot with
        {
            CatalogVersion = CatalogVersion,
            Results = Evaluate(snapshot.Facts)
        };
    }

    public IReadOnlyList<CapabilityProbeResult> Evaluate(IReadOnlyDictionary<string, string> facts) => registered.Select(definition =>
    {
        var value = facts.GetValueOrDefault(definition.FactKey, "unknown");
        return new CapabilityProbeResult(definition.Id, definition.Version, Status(definition, value), value);
    }).ToArray();

    public string[] Missing(string capabilitiesJson, IReadOnlyCollection<string> required, string? requiredScope = null)
    {
        if (required.Count == 0) return [];
        try
        {
            var snapshot = Normalize(Json.Read<CapabilitySnapshot>(capabilitiesJson));
            if (snapshot.Source != "probe" || snapshot.ObservedAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000 ||
                snapshot.ObservedAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120_000 ||
                (required.Contains("resource.workspace-disk", StringComparer.Ordinal) &&
                 (requiredScope is null || !string.Equals(snapshot.Scope, requiredScope, StringComparison.Ordinal))))
                return required.Order(StringComparer.Ordinal).ToArray();
            var available = snapshot.Results.Where(x => x.Status == CapabilityProbeStatus.Available)
                .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            return required.Where(x => !available.Contains(x)).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return required.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public CapabilitySnapshot? Read(string capabilitiesJson)
    {
        if (capabilitiesJson == "{}") return null;
        try { return Normalize(Json.Read<CapabilitySnapshot>(capabilitiesJson)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }

    private static string Status(CapabilityProbeDefinition definition, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return CapabilityProbeStatus.Unknown;
        if (definition.Kind == "Tool") return value == "present" ? CapabilityProbeStatus.Available : CapabilityProbeStatus.Unavailable;
        if (definition.Kind == "Resource")
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0
                ? CapabilityProbeStatus.Available : CapabilityProbeStatus.Unknown;
        if (definition.Kind == "Access") return value.ToLowerInvariant() switch
        {
            "available" or "present" or "true" => CapabilityProbeStatus.Available,
            "unavailable" or "absent" or "false" or "denied" => CapabilityProbeStatus.Unavailable,
            _ => CapabilityProbeStatus.Unknown
        };
        return definition.Id == "environment.scope" && value is not ("machine" or "container") ? CapabilityProbeStatus.Unknown : CapabilityProbeStatus.Available;
    }
}
