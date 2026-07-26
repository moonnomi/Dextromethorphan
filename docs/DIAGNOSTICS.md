# Developer diagnostics

Dextromethorphan has an opt-in local diagnostic session for performance and error analysis. It does not send telemetry or make network requests.

## Start a diagnostic session

From the repository root:

```powershell
.\scripts\Start-DiagnosticSession.ps1
```

Use the app normally, reproduce the lag or error, and close it. Closing finalizes two files:

- `diagnostics-...jsonl`: chronological timings, breadcrumbs, and structured errors.
- `diagnostics-...-summary.json`: aggregated count, errors, average, p50, p95, p99, and maximum time for every operation, plus process memory and GC counters.

For a short, detailed reproduction, include fast operations and artwork memory-cache hits:

```powershell
.\scripts\Start-DiagnosticSession.ps1 -VerboseTrace -Session folder-lag
```

Verbose mode creates substantially more events. Normal mode still aggregates every timing but writes individual timing events only when they take at least 2 ms.

## Direct executable options

The Release executable supports:

| Option | Purpose |
|---|---|
| `--diagnostics` | Enable local diagnostics. |
| `--diagnostics-verbose` | Include fast operations and cache-hit breadcrumbs. |
| `--diagnostics-output <directory>` | Choose the output directory. |
| `--diagnostics-session <name>` | Add a recognizable name to output files. |
| `--performance-overlay` | Open the local live performance overlay at startup. |

Equivalent environment variables are `DEXTROMETHORPHAN_DIAGNOSTICS`, `DEXTROMETHORPHAN_DIAGNOSTICS_VERBOSE`, `DEXTROMETHORPHAN_DIAGNOSTICS_OUTPUT`, `DEXTROMETHORPHAN_DIAGNOSTICS_SESSION`, and `DEXTROMETHORPHAN_PERFORMANCE_OVERLAY`.

Performance benchmark runs enable normal diagnostics automatically and place them beside the benchmark results.

## Instrumented operations

- SQLite library and playlist calls, including result/batch counts.
- Library refresh, group construction, and playlist-card construction.
- Tab, gallery, gallery-page, and track-list application.
- Artwork cache lookup/store/prune, file checks, memory-cache hits, and bitmap decode.
- Navigation command application and command-to-first-render latency.
- Process-to-first-render, library-ready, first-artwork, and interactive startup timing.
- Dispatcher exceptions, unobserved task exceptions, and fatal runtime errors.

Logging is performed by a bounded background channel. If a session produces events faster than they can be written, the summary reports `droppedEvents` instead of allowing diagnostics to stall the UI.

The first PERF-003 trace and its conclusions are recorded in [PERF-003 trace analysis](performance/PERF-003-TRACE-2026-07-25.md).

## Live performance overlay

PERF-004 adds an opt-in overlay for investigating visible stutter without leaving the app. Open it with the **FPS** button beside Audio diagnostics or press `Ctrl+Shift+F12`. The overlay subscribes to WPF frame events only while it is visible and shows:

- current and recent-average frame time plus effective FPS;
- UI-thread frames over 50 ms, including the worst frame;
- active and queued artwork decodes plus stale requests dropped before decode;
- decoded artwork cache entries, memory use, and hit rate;
- process working set and managed heap;
- generation 0, 1, and 2 garbage-collection counts.

The overlay is local-only and does not enable telemetry. When a diagnostic session is already active, detected UI stalls are also written to the JSONL trace as `render.ui-thread-stall`.

To show it immediately at startup:

```powershell
.\src\Dextromethorphan.App\bin\latest\Dextromethorphan.exe --performance-overlay
```

The equivalent environment variable is `DEXTROMETHORPHAN_PERFORMANCE_OVERLAY=1`. Close the overlay when it is not needed so frame sampling is fully detached.

## Create a support bundle

```powershell
.\scripts\Collect-Diagnostics.ps1
```

The bundle contains diagnostic/error logs, a redacted settings file, .NET and Windows context, Git revision/status, and available performance summaries. It intentionally excludes the music database, artwork, and audio files.

Review a bundle before sharing it. Exception records can contain local media paths and source-code paths.
