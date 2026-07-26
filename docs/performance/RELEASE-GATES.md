# Performance release gates

PERF-005 converts the consumer-readiness targets into checks that run against a `Measure-PerformanceBaseline.ps1` summary. These are release gates, not aspirational labels: a failed check blocks a performance-qualified release until it is fixed or the gate is deliberately revised with measurements and rationale.

The tracked thresholds are in [release-gates.json](release-gates.json).

| Gate | Threshold | Applies to |
|---|---:|---|
| Cold process to interactive | < 3,000 ms | 10k fixture |
| Cached tab switch, worst measured sample | < 100 ms | All fixtures |
| Album scroll p95 frame | ≤ 16.67 ms | All fixtures |
| Album scroll worst routine frame | ≤ 50 ms | All fixtures |
| Album scroll frames over 50 ms | 0 | All fixtures |
| Idle CPU | < 5% | All fixtures |
| Peak working set | < 300 MiB | 50k fixture |

The 16.67 ms p95 threshold represents sustained 60 Hz scrolling. Startup is evaluated only on the deterministic 10k fixture, and the memory gate only on the deterministic 50k fixture. Navigation, scrolling, and CPU gates apply to both.

## Run the gates

Capture the standard four-process baseline:

```powershell
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 10000
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 50000
```

Then evaluate each generated summary:

```powershell
.\scripts\Test-PerformanceGates.ps1 -Summary .\performance-results\<10k-run>\summary.json
.\scripts\Test-PerformanceGates.ps1 -Summary .\performance-results\<50k-run>\summary.json
```

The checker prints every applicable result and exits with code `1` when any gate fails, making it suitable for a local release script or CI. Use `-ReportOnly` while tuning to print failures without returning a failing exit code.

Benchmarks must run in Release, with the window visible and unobstructed, on the designated performance machine. A true cold-start qualification should be captured after reboot because the harness deliberately does not purge the Windows standby list.

## Current status

The 2026-07-26 10k measurement passes cold startup and routine-frame safety, but remains blocked by cached Songs-tab latency, 60 Hz p95 scrolling, and an idle-CPU sample slightly above 5%. The 50k memory gate must also be rerun after the latest artwork/virtualization changes. Defining the gates does not mark those remaining optimization problems as solved.
