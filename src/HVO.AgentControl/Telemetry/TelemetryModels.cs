namespace HVO.AgentControl.Telemetry;

/// <summary>
/// Confidence state of a single telemetry calculation. Guarded outcomes never fabricate
/// percentages: consuming sides must treat a non-<see cref="TelemetryState.OK"/> result
/// as "not live utilization".
/// </summary>
public enum TelemetryState
{
    OK,
    NeedsSecondSample,
    IdentityChanged,
    CounterReset,
    InvalidElapsed,
    Stale,
    InvalidInput,
    ArithmeticOverflow,
    NonFiniteResult,
    CpuUnavailable,
    QuotaUnavailable,
    MemoryLimitUnknown
}

/// <summary>
/// One observed sample: a cumulative CPU counter, the current memory usage/limit, and the
/// capacity facts needed to normalize them. <see langword="null"/> fields are deliberate
/// "unknown" values and are preserved explicitly instead of being coerced to zero.
/// </summary>
/// <param name="ObservedAt">Observation time as Unix epoch milliseconds.</param>
/// <param name="Identity">Identity of the observed runtime session; changes across reconnects.</param>
/// <param name="CumulativeCpuUsec">Monotonic cumulative CPU time consumed in microseconds.</param>
/// <param name="MemoryBytes">Current memory usage in bytes, or <see langword="null"/> when unknown.</param>
/// <param name="MemoryLimitBytes">Memory limit in bytes, or <see langword="null"/> when unbounded/unknown.</param>
/// <param name="QuotaCores">Equivalent CPU quota in cores (quota/period), or <see langword="null"/> when no quota applies.</param>
/// <param name="Platform">"linux", "macos", or "unknown".</param>
/// <param name="HostLogicalCores">Host-wide logical CPU count; a host metric, never used for quota normalization.</param>
public sealed record RuntimeTelemetrySample(
    long ObservedAt,
    string Identity,
    long? CumulativeCpuUsec,
    long? MemoryBytes,
    long? MemoryLimitBytes,
    double? QuotaCores,
    string Platform,
    int? HostLogicalCores);

/// <summary>
/// Deterministic result of combining two consecutive samples. Raw values are preserved even
/// when a guard blocks percentage fabrication so operators can still inspect the latest data.
/// </summary>
/// <param name="State">The guard that applied (or <see cref="TelemetryState.OK"/>).</param>
/// <param name="ObservedAt">The newer sample's observation time, Unix epoch milliseconds.</param>
/// <param name="CpuQuotaPercent">Container-quota-normalized CPU percent, or <see langword="null"/>. Values above 100 are preserved.</param>
/// <param name="CpuCoreUsage">Absolute CPU usage in cores (CPU seconds per wall second); distinct from quota percent.</param>
/// <param name="MemoryPercent">Current memory percent, or <see langword="null"/> when no limit is known. Values above 100 are preserved.</param>
/// <param name="QuotaCores">The quota used for normalization, in cores.</param>
/// <param name="CpuWindowMs">Wall time between the two samples in milliseconds.</param>
/// <param name="MemoryBytes">Newest raw memory usage in bytes, preserved even when percent is unknown.</param>
/// <param name="MemoryLimitBytes">Newest raw memory limit in bytes, preserved even when percent is unknown.</param>
/// <param name="Note">Readable explanation of the state, or <see langword="null"/>.</param>
public sealed record RuntimeTelemetry(
    TelemetryState State,
    long ObservedAt,
    double? CpuQuotaPercent,
    double? CpuCoreUsage,
    double? MemoryPercent,
    double? QuotaCores,
    long? CpuWindowMs,
    long? MemoryBytes,
    long? MemoryLimitBytes,
    string? Note)
{
    /// <summary>True only when the calculation completed and percentages were not guarded.</summary>
    public bool IsUsable => State == TelemetryState.OK;
}

/// <summary>
/// Durable calculated telemetry for one runtime observation. This is append-only history;
/// collectors are responsible for obtaining samples and passing calculated results to the store.
/// </summary>
public sealed class RuntimeTelemetryHistoryRecord
{
    public long Sequence { get; set; }
    public string RuntimeId { get; set; } = "";
    public long ObservedAt { get; set; }
    public string State { get; set; } = "";
    public double? CpuQuotaPercent { get; set; }
    public double? CpuCoreUsage { get; set; }
    public double? MemoryPercent { get; set; }
    public double? QuotaCores { get; set; }
    public long? CpuWindowMs { get; set; }
    public long? MemoryBytes { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public string? Note { get; set; }
}
