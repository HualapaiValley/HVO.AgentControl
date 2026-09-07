# Runtime telemetry design (issue #35 foundation)

Status: calculation foundation only. This change adds dedicated types, a parser, a
deterministic calculator, documentation, and unit tests. It deliberately adds **no polling
transport, database/migration, Program, or UI changes**. A future authenticated sampler will
feed the calculator; wiring that transport is outside this slice.

## Purpose and boundaries

Goal: derive runtime CPU/memory utilization from two timestamped cumulative CPU samples and a
current memory reading, normalized against the **container's CPU quota** where one applies.

Guarantees:

- Container **quota-normalized percent** is a separate figure from **absolute core usage** and
  from **host metrics**; none is silently substituted for another.
- The following must **never fabricate a percentage**: a single (first) sample, a runtime
  identity change between samples, a non-positive window, a cumulative counter that went
  backwards, a window beyond the staleness bound, a missing CPU counter, and an unavailable
  quota or memory limit.
- Unknown/stale data is **preserved explicitly** (raw bytes/limits remain on the result) while
  the computed percentages are reported as `null`.
- Static enrollment facts are capacity, not utilization. A single probe snapshot therefore
  yields `NeedsSecondSample`, never a percentage.

## Types

- `RuntimeTelemetrySample` — one observed sample (namespace `HVO.AgentControl.Telemetry`).
- `RuntimeTelemetry` — guarded result of combining two samples.
- `TelemetryState` — enum of results: `OK`, `NeedsSecondSample`, `IdentityChanged`,
  `CounterReset`, `InvalidElapsed`, `Stale`, `CpuUnavailable`, `QuotaUnavailable`,
  `MemoryLimitUnknown`.
- `TelemetryParser` — parses CapabilityProbe-shaped facts or tab-separated probe lines.
- `TelemetryCalculator` — deterministic `Compute(previous, current, options?)`; options carry
  `MaxStaleMs` (default 60 000).

### Calculator output

| Field | Meaning |
| --- | --- |
| `CpuQuotaPercent` | container-quota-normalized CPU percent in [0, 100]; `null` when not computable |
| `CpuCoreUsage` | absolute CPU cores (CPU seconds per wall second); distinct from percent |
| `MemoryPercent` | current memory percent in [0, 100]; `null` when the limit is unknown |
| `CpuWindowMs` | wall time between the two samples |
| `MemoryBytes` / `MemoryLimitBytes` | newest raw values, preserved even when percentages are guarded |
| `State` / `Note` | why the guard applied |

### Guard order (deterministic)

1. previous sample missing → `NeedsSecondSample`
2. identity differs → `IdentityChanged`
3. `ObservedAt` window ≤ 0 → `InvalidElapsed`
4. window > `MaxStaleMs` → `Stale`
5. CPU counter missing on either sample → `CpuUnavailable`
6. cumulative CPU went backwards → `CounterReset`
7. no positive `QuotaCores` → `QuotaUnavailable` (core usage still reported)
8. no positive memory limit (or missing current bytes) → `MemoryLimitUnknown`
9. otherwise → `OK`

Guards 1–6 suppress **all** percentages. Guards 7–8 suppress only the normalization they make
impossible; the other figure is still computed.

### Formulas

- `CpuCoreUsage = (toCpuUsec - fromCpuUsec) / 1000.0 / windowMs`
- `CpuQuotaPercent = min(100, CpuCoreUsage / quotaCores * 100)`
- `MemoryPercent = min(100, memoryBytes / memoryLimitBytes * 100)`

All arithmetic is `double`, no clock or environment dependency, so the calculator is
deterministic for identical inputs (covered by fixed-value unit tests).

## Input contract

`TelemetryParser.Parse(facts, observedAt, identity)` accepts `string→string` facts. Two key
families are accepted: canonical keys and the raw keys CapabilityProbe already emits. Aliases
are checked in the order shown; the first parseable value wins.

### Cumulative CPU (canonical: `accumCpuUsec`)

Microseconds of monotonic cumulative CPU time for the observed runtime.

- `accumCpuUsec` — canonical (future sampler output).
- `usageUsec` — cgroup v2 `cpu.stat / cpuacct`, already microseconds.
- `procCpuUsec` / `cpuUsageUsec` — process counter reported in microseconds.

Non-numeric, empty, or missing → `null` → CPU utilization is `CpuUnavailable`, never a guess.

### Memory (canonical: `memoryCurrentBytes`, `memoryLimitBytes`)

- `memoryCurrentBytes` — canonical current usage.
- `memoryCurrentV2` — `/sys/fs/cgroup/memory.current` (bytes).
- `memoryUsageV1` — `/sys/fs/cgroup/memory/memory.usage_in_bytes`.
- `memoryLimitBytes` — canonical limit.
- `memoryLimitV2` — `/sys/fs/cgroup/memory.max`; literal `max` → no limit (`null`).
- `memoryLimitV1` — `/sys/fs/cgroup/memory/memory.limit_in_bytes`; values ≥ `1<<62` (the Linux
  v1 "no limit" sentinel, e.g. `9223372036854771712`) → no limit (`null`).

**`memoryBytes` (macOS `hw.memsize`) is intentionally NOT mapped** to `MemoryBytes`: it is a
host metric, not a per-runtime memory reading, and using it would pretend host memory is the
runtime's utilization.

### CPU quota (canonical: `cpuQuotaCores`)

Equivalent cores = quota/period. Linux only.

- `cpuQuotaCores` — canonical decimal cores, as reported by a future sampler.
- `cpuQuotaV2` — `/sys/fs/cgroup/cpu.max`, two tokens `quota period`; quota token `max` means
  "no quota" (`null`); otherwise `quota/period`. Non-positive quota/period → `null`.
- `cpuQuotaMicrosV1` + `cpuPeriodMicrosV1` — `/sys/fs/cgroup/cpu/cpu.cfs_quota_us` and
  `cpu.cfs_period_us`; quota `-1` means "no quota" (`null`); otherwise `quota/period`.

`cpuset.cpus.effective` (`cpuSetV2`) restricts which CPUs may run; it does **not** define a
quota and is ignored by the parser (documented so it is not mistaken for capacity).

### Platform

- `os`: `linux` or `Darwin` (case-insensitive) selects `linux`/`macos`; anything else → `unknown`.
- `logicalCores`: host CPU count; carried on the sample as `HostLogicalCores` for operator
  context and **never used** in quota or memory normalization.

## Linux contracts

### cgroup v2

| Key | File | Units | No-limit marker |
| --- | --- | --- | --- |
| `cpuQuotaV2` | `/sys/fs/cgroup/cpu.max` | two numbers, `quota period`, microseconds | quota token `max` |
| `memoryLimitV2` | `/sys/fs/cgroup/memory.max` | bytes | literal `max` |
| `memoryCurrentV2` | `/sys/fs/cgroup/memory.current` | bytes | — |
| `usageUsec` | `/sys/fs/cgroup/cpu.stat` | microseconds cumulative | — |
| `cpuSetV2` | `/sys/fs/cgroup/cpuset.cpus.effective` | cpu list, informational only | — |

Quota percent only when `cpuQuotaV2` yields a positive quota and `quota != max`. A v2 container
with `max` quota is `QuotaUnavailable` for the percent but still has core usage.

### cgroup v1

| Key | File | Units | No-limit marker |
| --- | --- | --- | --- |
| `cpuQuotaMicrosV1` | `/sys/fs/cgroup/cpu/cpu.cfs_quota_us` | microseconds | `-1` |
| `cpuPeriodMicrosV1` | `/sys/fs/cgroup/cpu/cpu.cfs_period_us` | microseconds | — |
| `memoryLimitV1` | `/sys/fs/cgroup/memory/memory.limit_in_bytes` | bytes | sentinel ≥ `1<<62` |
| `memoryUsageV1` | `/sys/fs/cgroup/memory/memory.usage_in_bytes` | bytes | — |

## macOS contract

- No cgroup quota contract: `QuotaCores` stays `null` and `CpuQuotaPercent` is always `null`
  on `os=Darwin` unless a future sampler supplies a canonical `cpuQuotaCores`.
- `hw.memsize` (`memoryBytes`) is host memory; it is never mapped to `MemoryBytes`, so no
  memory percent is fabricated for a runtime from host memory.
- The CPU counter for macOS must come from a future sampler (`accumCpuUsec`); the parser does
  not invent one.

## Static enrollment is not live utilization

The CapabilityProbe script emits static cgroup capacity facts (`cpuQuotaV2`, `memoryLimitV2`,
`cpuQuotaMicrosV1`, `cpuPeriodMicrosV1`, `memoryLimitV1`). Those define what a runtime *may*
use; they say nothing about current usage. Utilization requires at least two cumulative CPU
samples, so a single snapshot always yields `NeedsSecondSample` — the calculator never turns a
static quota into a "usage" percentage. The future authenticated sampler provides the
timestamped cumulative counters; this slice only prepares the contract, parser, and arithmetic.

## Remaining issue #35 scope (not done here)

- Authenticated sampling transport and its authorization.
- Persistence/migration of samples or computed telemetry.
- Surfaces (Program/UI) that display runtime telemetry.
- Per-worker breakdown if desired.