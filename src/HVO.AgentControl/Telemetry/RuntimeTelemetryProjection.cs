using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;

namespace HVO.AgentControl.Telemetry;

public sealed record RuntimeTelemetryProjection(RuntimeTelemetrySample Sample, RuntimeTelemetry Result)
{
    public static RuntimeTelemetryProjection? FromCapabilities(string capabilitiesJson, string identity)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<CapabilitySnapshot>(capabilitiesJson, Json.Options);
            if (snapshot is null || snapshot.ObservedAt <= 0 || snapshot.Facts is null) return null;
            if (snapshot.Facts.TryGetValue("probe", out var probe) &&
                (string.Equals(probe?.Trim(), "unavailable", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(probe?.Trim(), "unsupported", StringComparison.OrdinalIgnoreCase))) return null;
            var sample = TelemetryParser.Parse(snapshot.Facts, snapshot.ObservedAt, identity);
            return new(sample, TelemetryCalculator.Compute(null, sample));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
