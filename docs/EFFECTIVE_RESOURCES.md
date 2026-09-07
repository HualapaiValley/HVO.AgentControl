# Effective resource normalization

Issue #19 normalizes host-visible and cgroup facts into two derived probe facts:

- `effectiveCpuCores` — the minimum of host-visible logical cores, the cgroup CPU
  quota (v2 `cpu.max` or v1 `cpu.cfs_quota_us`/`cpu.cfs_period_us`), and the
  effective cpuset. Fractional quotas are preserved (e.g. `150000 100000` -> `1.5`).
- `effectiveMemoryBytes` — the minimum of host-visible memory and the cgroup memory
  limit (v2 `memory.max` or v1 `memory.limit_in_bytes`).

Raw measured facts and the `ObservedAt` timestamp stay untouched; `unknown` is
emitted whenever no value is verifiable. Nothing that cannot be measured is claimed
verified.

Normalization rules:

- `max` / `-1` (or v1 values `<= 0` and >= `1 << 62`, the "no limit" sentinels) mean
  unlimited and contribute no constraint.
- Malformed quota/period/cpuset/memory values contribute no constraint and never
  crash the probe.
- Non-container and macOS inputs fall back to the host-visible values (`logicalCores`,
  `memoryKiB`, `memoryBytes`).
- Host memory KiB is multiplied by 1024 with overflow guarding; overflowing input
  yields `unknown`.

## Measured beta container limits (read-only, 2026-09-07)

| Fact | Value | Interpretation |
|---|---|---|
| cgroup version | cgroup2fs | v2 hierarchy |
| `cpu.max` | `200000 100000` | quota/period = 2.0 CPUs hard cap |
| `cpuset.cpus.effective` | `0-7` | 8 CPUs visible |
| `memory.max` | `4294967296` | 4 GiB hard memory cap |
| `memory.high` | `max` | no soft reclaim limit |
| host `nproc` | 8 | host-visible CPU count |
| host `MemTotal` | 24608548 KiB (23.47 GiB) | host-visible memory |
| `/.dockerenv` | present | container execution scope |

Effective normalization result: `effectiveCpuCores = 2`, `effectiveMemoryBytes = 4294967296` (4 GiB).