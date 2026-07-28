# Concurrent scan, playback, and navigation

PERF-GATE-004 now has a reproducible cold-run workload instead of relying on separate scan and playback samples.

## Workload

- Generate and play 30 seconds of 44.1 kHz, 16-bit stereo PCM through shared, event-driven WASAPI.
- Import 1,000 generated WAV files into an isolated SQLite library.
- Switch through Albums, Artists, Genres, Songs, Folders, and Playlists while the scan and playback are both active.
- Observe every audio state notification plus periodic playback snapshots.

The gate passes only when:

- all files import with zero failures;
- playback remains in `Playing`, reports no error or buffering/fault transition, and advances by at least 250 ms;
- the maximum primary-tab switch remains below 100 ms.

## 10k fixture result

| Metric | Result |
|---|---:|
| Files imported | 1,000 / 1,000 |
| Scan failures | 0 |
| Scan elapsed | 598.3 ms |
| Playback interruptions | 0 |
| Playback position advanced | 650.2 ms |
| Concurrent navigation p95 | 81.2 ms |
| Concurrent navigation maximum | 81.2 ms |
| Gate | PASS |

The JSON report schema is version 4. The baseline summary and release-gate script surface the combined result as `concurrentWorkloadPassed`.
