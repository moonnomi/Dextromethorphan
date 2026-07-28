# Final performance gate report â€” 2026-07-28

Milestone 1 is qualified against the deterministic seed `20260725` 10k- and 50k-track fixtures. Each fixture ran in four fresh Release processes: one cold process and three warm processes. The benchmark window remained visible and unobstructed on the designated Windows x64 machine with a 144 Hz display.

## Results

| Gate | Budget | 10k result | 50k result |
|---|---:|---:|---:|
| Cold start to interactive | < 3,000 ms at 10k | 1,585.713 ms | 2,586.894 ms (reported, not gated) |
| Cached tab, worst per-view median | < 100 ms | 75.631 ms | 76.458 ms |
| Cached tab, raw worst sample | Diagnostic | 85.795 ms | 87.976 ms |
| Album-scroll p95 | â‰¤ 16.67 ms | 13.613 ms | 10.864 ms |
| Album-scroll worst frame | â‰¤ 50 ms | 27.346 ms | 30.922 ms |
| Album-scroll frames over 50 ms | 0 | 0 | 0 |
| Idle CPU | < 6% | 5.422% | 5.329% |
| Maximum settled working set | < 300 MiB at 50k | 250.1 MiB (reported, not gated) | 292.3 MiB |
| Navigation history restoration | Pass | Pass | Pass |
| Hidden-view artwork release | Pass | Pass | Pass |
| Paged Songs presentation | Pass | Pass | Pass |
| Concurrent scan/playback/navigation | Pass | Pass | Pass |

Both gate checkers returned **10/10 PASS**.

## Concurrent workload evidence

The cold workload played generated 44.1 kHz, 16-bit stereo PCM through shared WASAPI while scanning 1,000 generated media files and switching through every primary tab.

| Metric | 10k | 50k |
|---|---:|---:|
| Playback position advanced | 1,021.678 ms | 1,021.678 ms |
| Playback interruptions | 0 | 0 |
| Scan imported / failed | 1,000 / 0 | 1,000 / 0 |
| Concurrent navigation maximum | 76.993 ms | 94.458 ms |
| Isolated scan throughput | 4,595.99 files/s | 4,473.04 files/s |

## Reproduce

```powershell
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 10000 -WarmRuns 3 -Output .\performance-results\release-gate-final-10k-20260728
.\scripts\Test-PerformanceGates.ps1 -Summary .\performance-results\release-gate-final-10k-20260728\summary.json

.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 50000 -WarmRuns 3 -Output .\performance-results\release-gate-final-50k-20260728
.\scripts\Test-PerformanceGates.ps1 -Summary .\performance-results\release-gate-final-50k-20260728\summary.json
```

Local source summaries:

- `performance-results/release-gate-final-10k-20260728/summary.json`
- `performance-results/release-gate-final-50k-20260728/summary.json`

The generated result directories are intentionally not committed. The fixtures, scripts, thresholds, and this evidence report are tracked so the qualification remains reproducible.

## Optimization outcome

The release pass combines asynchronous/off-thread artwork decoding, bounded strong and persistent thumbnail caches, active-view request planning, generator-backed gallery recycling, row-aware layout invalidation, idle-only gallery paging, paged Songs, and 32-card Folder/Playlist presentation windows. The return-to-top benchmark also verifies that recycling restores the correct top-row item/data-context mappings, covering the prior disappearing-gallery regression.
