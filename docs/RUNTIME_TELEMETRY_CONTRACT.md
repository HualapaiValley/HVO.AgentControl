# Runtime telemetry calculation contract

Implementation for issue #35 calculation foundation (namespace `HVO.AgentControl.Telemetry`).
This document defines the input facts, parser behavior, calculator guard states, and the
deterministic output produced from two consecutive samples. Runtime cards now project the
existing authenticated capability snapshot through this contract, showing available memory and
quota facts while explicitly retaining `NeedsSecondSample`. The periodic collector described
below adds sampled history for supported Linux containers.

It supplements the broader [worker status and observability design](OBSERVABILITY_DESIGN.md)
without replacing that document's status and usage requirements.

## Scope and guarantees

- Container **quota-normalized percent** is separate from **absolute core usage** and from
  **host metrics**; none is silently substituted for another.
- The following must **never produce a percentage**: a single (first) sample, identity change,
  non-positive window, counter reset, stale window, missing CPU counter, unavailable quota,
  unavailable memory limit, or non-finite computed values.
- Unknown/stale data is **preserved explicitly** (raw bytes remain on the result) while the
  guarded figures are reported as `null`.
- Negative CPU/memory counters, `NaN`, `Infinity`, and `-Infinity` in input facts are rejected
  by the parser (produce `null`).
- Quota percent and memory percent are **never clamped**. When the measured ratio exceeds 100%
  of the current sample's quota/limit, the actual ratio is preserved with a note explaining why
  the normalization may be unreliable (quota may have changed during the window, effective
  allocation may differ from cgroup quota, or kernel overcommit may exceed a limit).
- The `Stale` guard measures the **window width** between the two samples, not the age of the
  newer sample relative to the current clock. The sampling caller must separately assess
  freshness (e.g. `current.ObservedAt` vs "now") at read/display time.
- Static enrollment facts (quota, limits) are capacity, not utilization. A single probe
  snapshot always yields `NeedsSecondSample`.

## Types

| Type | Role |
| --- | --- |
| `RuntimeTelemetrySample` | One observed sample (cumulative CPU, memory, capacity). |
| `RuntimeTelemetry` | Guarded result of combining two samples. |
| `TelemetryState` | Enum: `OK`, `NeedsSecondSample`, `IdentityChanged`, `CounterReset`, `InvalidElapsed`, `Stale`, `CpuUnavailable`, `QuotaUnavailable`, `MemoryLimitUnknown`. |
| `TelemetryParser` | Parses CapabilityProbe facts (dictionary or tab-lines) into a sample. |
| `TelemetryCalculator` | `Compute(previous, current, options?)` — deterministic, clock-free. |
| `TelemetryCalculatorOptions` | `MaxStaleMs` (default 60 000) — window-width staleness bound. |

### RuntimeTelemetry output

| Field | Meaning |
| --- | --- |
| `CpuQuotaPercent` | Quota-normalized CPU percent (**unclamped**); `null` when not computable |
| `CpuCoreUsage` | Absolute CPU cores (CPU seconds per wall second); distinct from percent |
| `MemoryPercent` | Memory percent (**unclamped**); `null` when the limit is unknown |
| `QuotaCores` | The quota used for normalization |
| `CpuWindowMs` | Wall time between samples (ms) |
| `MemoryBytes` / `MemoryLimitBytes` | Newest raw values, preserved even when percent is guarded |
| `State` / `Note` | Why the guard applied, or null when OK |

When `CpuQuotaPercent > 100` or `MemoryPercent > 100`, a note is attached. This is **not an
error state** — it is the honest measured ratio against the current quota/limit. The note
explains possible causes: quota/limit change during the window, cpuset/ancestor effective
capacity differing from the direct cgroup quota, or kernel memory overcommit.

CPU quota normalization is produced only when both samples contain the same positive quota.
Any transition between unavailable and available quota, or between two different quotas,
suppresses `CpuQuotaPercent` for that interval while retaining absolute core usage.

### Guard order (deterministic)

1. `previous` missing → `NeedsSecondSample`
2. identity differs → `IdentityChanged`
3. `ObservedAt` window ≤ 0 → `InvalidElapsed`
4. window > `MaxStaleMs` → `Stale`
5. CPU counter missing → `CpuUnavailable`
6. cumulative CPU went backwards → `CounterReset`
7. non-positive or non-finite `QuotaCores` → `QuotaUnavailable`
8. memory percent not computable → `MemoryLimitUnknown`
9. otherwise → `OK`

Guards 1–6 suppress all percentages. Guards 7–8 suppress only the normalization they affect.

### Formulas

- `CpuCoreUsage = (toCpuUsec - fromCpuUsec) / 1000.0 / windowMs`
- `CpuQuotaPercent = CpuCoreUsage / quotaCores * 100` (no clamping)
- `MemoryPercent = memoryBytes / memoryLimitBytes * 100` (no clamping)

The calculator verifies that intermediate and final values are finite before reporting them as
percentages. Non-finite results (from extreme inputs) are guarded by returning the appropriate
unavailable state.

## Input contract

`TelemetryParser.Parse(facts, observedAt, identity)` accepts `string→string` facts. Two key
families are accepted: canonical keys and the raw keys `CapabilityProbe` already emits. Aliases
are checked in the order shown; the first parseable value wins.

Raw `*V1`/`*V2` cgroup keys are honored only when `os` identifies Linux. Canonical keys such as
`cpuQuotaCores`, `memoryCurrentBytes`, and `memoryLimitBytes` are explicit sampler inputs and
may be used on another platform. If both cgroup versions are present, an explicit v2 value is
authoritative: `memoryLimitV2=max` means no limit and does not fall back to a v1 limit.

**Boundary rules applied by the parser:**
- Negative CPU counters → `null` (cumulative CPU is monotonic by definition).
- Negative memory bytes → `null` (memory usage cannot be negative).
- Negative memory limits → `null` (limits cannot be negative).
- `NaN`, `Infinity`, `-Infinity` in quota parsing → `null`.
- Non-positive quota values → `null`.

### Cumulative CPU (canonical: `accumCpuUsec`)

Microseconds of monotonic cumulative CPU time for the observed runtime.

- `accumCpuUsec` — canonical (future sampler output).
- `usageUsec` — cgroup v2 `cpu.stat / cpuacct`, already microseconds.
- `procCpuUsec` / `cpuUsageUsec` — process counter in microseconds.

Negative values are rejected.

### Memory (canonical: `memoryCurrentBytes`, `memoryLimitBytes`)

- `memoryCurrentBytes` → `MemoryBytes`; aliases: `memoryCurrentV2`, `memoryUsageV1`.
- `memoryLimitBytes` → `MemoryLimitBytes`; aliases: `memoryLimitV2` (literal `max` → null),
  `memoryLimitV1` (sentinel ≥ `1<<62` → null).

`memoryBytes` (macOS `hw.memsize`) is **not** mapped to `MemoryBytes`: it is a host metric.

Negative values are rejected.

### CPU quota (canonical: `cpuQuotaCores`)

Equivalent cores = quota/period. Linux only.

- `cpuQuotaCores` — canonical decimal cores.
- `cpuQuotaV2` — `/sys/fs/cgroup/cpu.max`, two tokens `quota period`; `max` → null.
- `cpuQuotaMicrosV1` + `cpuPeriodMicrosV1` — v1 path; quota `-1` → null.

`cpuset.cpus.effective` (`cpuSetV2`) restricts which CPUs may run; it does **not** define a
quota and is not parsed as capacity.

Non-finite or non-positive values are rejected.

### Platform

- `os`: `linux` or `Darwin` (case-insensitive) → `linux`/`macos`; anything else → `unknown`.
- `logicalCores`: host CPU count; carried as `HostLogicalCores` for operator context, never
  used in normalization.

## Linux cgroup v2

| Key | File | Units | No-limit marker |
| --- | --- | --- | --- |
| `cpuQuotaV2` | `/sys/fs/cgroup/cpu.max` | `quota period` (µs) | quota token `max` |
| `cpuSetV2` | `/sys/fs/cgroup/cpuset.cpus.effective` | cpu list, informational | — |
| `memoryLimitV2` | `/sys/fs/cgroup/memory.max` | bytes | literal `max` |
| `memoryCurrentV2` | `/sys/fs/cgroup/memory.current` | bytes | — |
| `usageUsec` | `/sys/fs/cgroup/cpu.stat` | µs cumulative | — |

## Linux cgroup v1

| Key | File | Units | No-limit marker |
| --- | --- | --- | --- |
| `cpuQuotaMicrosV1` | `/sys/fs/cgroup/cpu/cpu.cfs_quota_us` | µs | `-1` |
| `cpuPeriodMicrosV1` | `/sys/fs/cgroup/cpu/cpu.cfs_period_us` | µs | — |
| `memoryLimitV1` | `/sys/fs/cgroup/memory/memory.limit_in_bytes` | bytes | sentinel ≥ `1<<62` |
| `memoryUsageV1` | `/sys/fs/cgroup/memory/memory.usage_in_bytes` | bytes | — |

## macOS

- No cgroup quota: `QuotaCores` stays `null`, `CpuQuotaPercent` is always `null` unless a
  future sampler supplies canonical `cpuQuotaCores`.
- `hw.memsize` (`memoryBytes`) is host memory; not mapped to `MemoryBytes`.
- CPU counter must come from a future sampler (`accumCpuUsec`).

## Staleness semantics

`TelemetryState.Stale` indicates the **sample-to-sample window** exceeded `MaxStaleMs`. This is
a guard against measuring utilization over an unrepresentatively long interval — it does **not**
mean the newer sample is old relative to the current clock.

The sampling caller must separately check `current.ObservedAt` against the current time at
read/display time to determine whether the data is fresh enough for the intended use. The
calculator itself has no clock and makes no age-from-now judgment.

## Effective allocation vs cgroup quota

`QuotaCores` reflects the **direct cgroup quota** (quota/period from `cpu.max` or
`cpu.cfs_quota_us`/`cpu.cfs_period_us`). It does **not** account for:

- Ancestor cgroup limits (the container may be further restricted by a parent cgroup).
- `cpuset.cpus.effective` restrictions (which may limit to fewer cores than the quota implies).
- Host-level CPU throttling or oversubscription.

When `CpuQuotaPercent` exceeds 100%, this may indicate the effective allocation during the
measurement window differed from the current direct cgroup quota (e.g. the quota was expanded
after the window started, or the cpuset was wider). The measured ratio is preserved as-is; the
note explains why the normalization may be unreliable. The caller should not assume raw local
cgroup quota equals effective capacity.

## Remaining scope (not in this slice)

- Authenticated sampling transport and authorization.
- Persistence/migration of samples or computed telemetry.
- Live Program/UI utilization based on consecutive samples (runtime cards expose only the
  existing point-in-time capability observation).
- Per-worker breakdown.

## Periodic collection for managed Linux containers

The connection supervisor now owns a background sampler per connected runtime, starting a sample immediately and then waiting 30 seconds after each attempt. It uses a separate asynchronous loop, never overlaps probes, and cancels/drains that loop before disposing its transport. The SSH probe has a five-second command timeout. Probe/storage failures reset the comparison baseline and do not escape into prompt dispatch or runtime enrollment. A reconnect creates a new identity and starts with `NeedsSecondSample`; unsupported scopes produce no synthetic sample.

Initial live collection is deliberately limited to Linux containers with a private cgroup v2 root (`0::/`), a container marker and readable `cpu.stat`/`memory.current`. It reads cumulative `usage_usec`, quota/period from `cpu.max`, and memory current/max. No process enumeration, tool discovery, model call, installed daemon or Docker socket is needed. Values describe that container cgroup and descendants, not a host utilization estimate. Kernel field definitions: [cgroup v2 CPU and memory interfaces](https://docs.kernel.org/admin-guide/cgroup-v2.html).

Computed observations go into the bounded 200-row per-runtime history and existing authenticated history endpoint. The old capability card remains a labelled connection snapshot; consumers must use sampled history and its observation time for live trends. macOS, host-wide Linux and cgroup v1 collectors remain follow-up work in #35, rather than being presented as zero usage.

Validation: deterministic sampler tests cover stalled-probe cancellation, no late observation after cancellation, recovery from probe/storage failure, unsupported scope, baseline reset and counter units. A read-only execution on `hvo-agentcontrol-beta-dev3` returned its actual cumulative CPU counter, 2-core quota and 4 GiB memory limit. No live runtime was restarted or reconfigured for that check.
