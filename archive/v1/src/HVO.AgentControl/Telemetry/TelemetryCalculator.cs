namespace HVO.AgentControl.Telemetry;

/// <summary>Options controlling guarded telemetry calculations.</summary>
/// <param name="MaxStaleMs">Windows longer than this are preserved but not treated as live utilization.</param>
public sealed record TelemetryCalculatorOptions(long MaxStaleMs = 60_000);

/// <summary>
/// Deterministic CPU/memory utilization calculation from two consecutive samples. Every guard
/// below returns explicitly rather than fabricating a percentage: first sample, identity
/// change, non-positive window, counter reset, stale window, missing CPU counter, and
/// unavailable quota all suppress percentages. Container-quota-normalized percent is reported
/// separately from absolute core usage; host metrics are never merged into either. Ratios are
/// never clamped: an over-quota result remains visible with an explanatory note.
/// </summary>
public static class TelemetryCalculator
{
    public static RuntimeTelemetry Compute(RuntimeTelemetrySample? previous, RuntimeTelemetrySample current, TelemetryCalculatorOptions? options = null)
    {
        var maxStaleMs = (options ?? new TelemetryCalculatorOptions()).MaxStaleMs;

        if (maxStaleMs < 0) return Outcome(TelemetryState.InvalidInput, current, null, "MaxStaleMs cannot be negative.");
        if (Validate(current) is { } currentError)
            return Outcome(TelemetryState.InvalidInput, current, null, currentError);

        if (previous is null)
            return Outcome(TelemetryState.NeedsSecondSample, current, null, note: "One snapshot measures capacity, not utilization; awaiting a second cumulative sample.");

        if (Validate(previous) is { } previousError)
            return Outcome(TelemetryState.InvalidInput, current, null, previousError);

        if (previous.Identity != current.Identity)
            return Outcome(TelemetryState.IdentityChanged, current, null, note: "Runtime identity changed between samples (reconnect or new session); deltas are not comparable.");

        if (current.ObservedAt <= previous.ObservedAt)
            return Outcome(TelemetryState.InvalidElapsed, current, null, note: "Sample window is not positive; utilization is not computed.");

        long windowMs;
        try { windowMs = checked(current.ObservedAt - previous.ObservedAt); }
        catch (OverflowException) { return Outcome(TelemetryState.ArithmeticOverflow, current, null, "Sample timestamp subtraction overflowed; utilization is not computed."); }

        if (windowMs > maxStaleMs)
            return Outcome(TelemetryState.Stale, current, windowMs, note: "Window " + windowMs + "ms exceeds the " + maxStaleMs + "ms staleness bound; raw data is preserved but not treated as live utilization.");

        if (previous.CumulativeCpuUsec is not { } fromCpu || current.CumulativeCpuUsec is not { } toCpu)
            return Outcome(TelemetryState.CpuUnavailable, current, windowMs, note: "No cumulative CPU counter on both samples; utilization cannot be derived.");

        if (toCpu < fromCpu)
            return Outcome(TelemetryState.CounterReset, current, windowMs, note: "Cumulative CPU counter went backwards (reset or renewed counter); no percentage is fabricated.");

        long cpuUsec;
        try { cpuUsec = checked(toCpu - fromCpu); }
        catch (OverflowException) { return Outcome(TelemetryState.ArithmeticOverflow, current, windowMs, "Cumulative CPU subtraction overflowed; utilization is not computed."); }

        var coreUsage = cpuUsec / 1000.0 / windowMs;
        if (!double.IsFinite(coreUsage))
            return Outcome(TelemetryState.NonFiniteResult, current, windowMs, "CPU core usage was non-finite; utilization is not computed.");

        var previousQuota = previous.QuotaCores is { } previousValue && previousValue > 0 ? previousValue : (double?)null;
        var quota = current.QuotaCores is { } currentValue && currentValue > 0 ? currentValue : (double?)null;
        var quotaChanged = previousQuota != quota;
        var quotaPercent = quota is { } qc && !quotaChanged ? coreUsage / qc * 100 : (double?)null;
        if (quotaPercent is { } measuredQuotaPercent && !double.IsFinite(measuredQuotaPercent))
            return Outcome(TelemetryState.NonFiniteResult, current, windowMs, "CPU quota-normalized percent was non-finite; utilization is not computed.");

        var memoryPercent = current.MemoryBytes is { } mem && current.MemoryLimitBytes is { } limit && limit > 0
            ? mem / (double)limit * 100
            : (double?)null;
        if (memoryPercent is { } measuredMemoryPercent && !double.IsFinite(measuredMemoryPercent))
            return Outcome(TelemetryState.NonFiniteResult, current, windowMs, "Memory percent was non-finite; utilization is not computed.");

        TelemetryState state;
        string? note;
        if (quota is null || quotaChanged)
        {
            state = TelemetryState.QuotaUnavailable;
            note = quotaChanged
                ? "CPU quota changed between samples; current-quota normalization is suppressed (absolute core usage is reported)."
                : current.Platform == TelemetryParser.PlatformMacos
                ? "macOS has no cgroup quota contract; quota-normalized percent is not computed (absolute core usage is reported)."
                : "No applicable CPU quota limit is available; quota-normalized percent is not computed (absolute core usage is reported).";
        }
        else if (memoryPercent is null)
        {
            state = TelemetryState.MemoryLimitUnknown;
            note = "Memory limit is unavailable or not positive; memory percent is not computed.";
        }
        else
        {
            state = TelemetryState.OK;
            var notes = new List<string>();
            if (quotaPercent > 100) notes.Add("Measured CPU ratio exceeds 100% of the current direct cgroup quota; ancestor/cpuset effective allocation or a quota change may differ from this local value.");
            if (memoryPercent > 100) notes.Add("Measured memory ratio exceeds 100% of the current limit; the limit may have changed or kernel accounting may temporarily exceed it.");
            note = notes.Count == 0 ? null : string.Join(" ", notes);
        }

        return new RuntimeTelemetry(state, current.ObservedAt, quotaPercent, coreUsage, memoryPercent, quota, windowMs, current.MemoryBytes, current.MemoryLimitBytes, note);
    }

    private static RuntimeTelemetry Outcome(TelemetryState state, RuntimeTelemetrySample current, long? windowMs, string? note)
        => new(state, current.ObservedAt, null, null, null, null, windowMs, current.MemoryBytes, current.MemoryLimitBytes, note);

    private static string? Validate(RuntimeTelemetrySample sample)
    {
        if (sample.ObservedAt < 0) return "Sample timestamp cannot be negative.";
        if (sample.CumulativeCpuUsec is < 0) return "Cumulative CPU time cannot be negative.";
        if (sample.MemoryBytes is < 0) return "Current memory cannot be negative.";
        if (sample.MemoryLimitBytes is < 0) return "Memory limit cannot be negative.";
        if (sample.QuotaCores is < 0) return "CPU quota cannot be negative.";
        if (sample.QuotaCores is { } quota && !double.IsFinite(quota)) return "CPU quota must be finite.";
        if (sample.HostLogicalCores is < 0) return "Host logical cores cannot be negative.";
        if (sample.Identity is null || sample.Platform is null) return "Sample identity and platform are required.";
        return null;
    }
}
