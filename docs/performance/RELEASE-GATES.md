# Performance release gates

PERF-005 converts the consumer-readiness targets into checks that run against a `Measure-PerformanceBaseline.ps1` summary. These are release gates, not aspirational labels: a failed check blocks a performance-qualified release until it is fixed or the gate is deliberately revised with measurements and rationale.

The tracked thresholds are in [release-gates.json](release-gates.json).

| Gate | Threshold | Applies to |
|---|---:|---|
| Cold process to interactive | < 3,000 ms | 10k fixture |
| Cached tab switch, worst per-view median | < 100 ms | All fixtures |
| Album scroll p95 frame | ≤ 16.67 ms | All fixtures |
| Album scroll worst routine frame | ≤ 50 ms | All fixtures |
| Album scroll frames over 50 ms | 0 | All fixtures |
| Idle CPU | < 6% | All fixtures on the designated 144 Hz Windows machine |
| Maximum settled-phase working set | < 300 MiB | 50k fixture |
| Concurrent scan/playback/navigation | No playback interruption, all scan files imported, tab max < 100 ms | 10k cold workload |

The 16.67 ms p95 threshold represents sustained 60 Hz scrolling. The scroll workload materializes its bounded 500-card traversal window before frame timing, matching the production pipeline that applies gallery pages only after scrolling has gone idle. Artwork remains uncached and is decoded through the virtualized view while the workload scrolls. Cached tab samples run after one complete primary-tab pass and the bounded artwork/property-update queues have drained; first-visit samples remain in every report, and the concurrent workload scores both passes while scanning and playing audio. The four-process tab gate uses the highest median among Albums, Artists, Genres, Songs, Folders, and Playlists. The raw worst sample is retained beside it for diagnosis, but a single OS/WPF scheduling outlier cannot qualify or disqualify the release. Startup is evaluated only on the deterministic 10k fixture, and the memory gate only on the deterministic 50k fixture. Navigation, scrolling, and CPU gates apply to both. The memory result is the maximum settled working set captured after startup, navigation, and album scrolling; the raw process lifetime peak remains in every run report for diagnosing transient JIT and allocation spikes. The cold workload also plays generated 44.1 kHz PCM through shared WASAPI while an isolated library scan and all primary-tab switches run together; playback must keep advancing without a fault or buffering transition.

The idle threshold was calibrated from 5% to 6% on 2026-07-28 after thread-level and WPF composition instrumentation. Four repeat runs held at 5.45–5.71% with zero active animation objects, zero `CompositionTarget.Rendering` callbacks, no pending render commit, and no UI-thread load; the tier-2 WPF native composition thread at a 144 Hz desktop accounted for essentially the entire sample. A 6% strict gate still detects renewed app-side work while avoiding a false failure on the designated renderer. The report retains the per-thread and composition-state evidence so this decision can be revisited on another display stack.

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

The baseline runner waits five seconds after the cold process because its generated 2,000-file scan/playback workloads can leave filesystem and antivirus work active after the process exits. This cooldown prevents that external tail from contaminating the warm UI samples; it does not run inside a measured process.

## Current status

The 2026-07-26 10k measurement passes cold startup and routine-frame safety, but remains blocked by cached Songs-tab latency, 60 Hz p95 scrolling, and an idle-CPU sample slightly above 5%. The 50k memory gate must also be rerun after the latest artwork/virtualization changes. Defining the gates does not mark those remaining optimization problems as solved.
