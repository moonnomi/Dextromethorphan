# Performance fixtures

The performance-fixture tool creates deterministic, copyright-free library data for repeatable UI and repository measurements. It never reads or modifies the normal `%APPDATA%\Dextromethorphan` library.

## Profiles

| Profile | Tracks | Albums | Primary artists | Artwork | Manual playlists |
|---|---:|---:|---:|---:|---:|
| 10k | 10,000 | 500 | 100 | 500 procedural covers | 20 |
| 50k | 50,000 | 2,500 | 500 | 2,500 procedural covers | 100 |
| 100k | 100,000 | 5,000 | 1,000 | 5,000 procedural covers | 100 |

Multi-artist tracks add a small number of guest artist groups. Metadata also varies codec, sample rate, bit depth, genre, year, duration, ReplayGain, rating, love state, play count, last-played time, comments, and lyrics.

The generated media paths intentionally contain no audio files. These fixtures are for startup, navigation, search, grouping, artwork, scrolling, memory, and database benchmarks—not playback or decoder qualification.

## Generate a fixture

From the repository root:

```powershell
dotnet restore Dextromethorphan.slnx
dotnet build Dextromethorphan.slnx -c Debug --no-restore
.\scripts\New-PerformanceFixture.ps1 -Tracks 10000
.\scripts\New-PerformanceFixture.ps1 -Tracks 50000
.\scripts\New-PerformanceFixture.ps1 -Tracks 100000
```

If local PowerShell policy blocks repository scripts, invoke them through `powershell -NoProfile -ExecutionPolicy Bypass -File`.

The default seed is `20260725`. A different deterministic dataset can be selected explicitly:

```powershell
.\scripts\New-PerformanceFixture.ps1 -Tracks 10000 -Seed 42
```

By default, data is written beneath `performance-fixtures/`, which is ignored by Git. Pass `-Output` to place it elsewhere. Re-running against a populated directory requires `-Force`; the generator only replaces directories containing its safety marker and refuses arbitrary directories.

## Launch the isolated library

```powershell
dotnet build src\Dextromethorphan.App -c Release --no-restore
.\scripts\Start-PerformanceFixture.ps1 -Tracks 10000
```

The launcher sets `DEXTROMETHORPHAN_DATA_ROOT` only for that process and starts the normal app. Closing that process returns normal launches to `%APPDATA%\Dextromethorphan`.

The environment variable can also be used directly:

```powershell
$env:DEXTROMETHORPHAN_DATA_ROOT = "C:\path\to\fixture"
dotnet run --project src\Dextromethorphan.App -c Release
```

Do not point `DEXTROMETHORPHAN_DATA_ROOT` at the normal app-data directory while generating a fixture.

## Output

Each fixture contains:

- `library.db`: the real application schema with FTS indexes and deterministic library/playlist content.
- `artwork/`: unique 256×256 procedurally generated PNG covers stored with the app cache extension.
- `settings.json`: isolated settings with an empty scan-source list.
- `fixture.json`: profile counts, seed, layout parameters, and canonical content SHA-256.
- `.dextromethorphan-performance-fixture`: safety marker required before forced replacement.

The manifest hash covers canonical relative metadata, generated artwork bytes, and playlist membership. Generating the same profile and seed in different directories must produce the same content hash.

## Measure a baseline

Build the Release app, then run:

```powershell
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 10000
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 50000
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 100000
```

The benchmark opens the normal application against the isolated fixture. Keep the window visible and unobstructed until it closes automatically. Results are written beneath the ignored `performance-results/` directory as individual JSON runs, an aggregate `summary.json`, and a readable `summary.md`.

Each sequence records:

- process-to-window, first-render, first-artwork, library-ready, and interactive startup timing;
- first and cached switches through Albums, Artists, Genres, Songs, Folders, and Playlists;
- Mouse4/Mouse5 history latency plus collection identity, selection, materialized-page, and scroll-offset restoration;
- hidden-view artwork-source release after a loaded gallery is collapsed;
- initial and next-page Songs materialization versus the full source count;
- 180 real WPF rendering intervals while the album gallery loads and scrolls;
- working set, peak working set, managed heap, and GC counts;
- normalized idle CPU;
- a 1,000-file generated WAV scan, including failures and throughput;
- normalized playback CPU using 44.1 kHz 16-bit generated silence.

The first process is labeled cold and subsequent fresh processes are labeled warm. The runner does not purge the Windows standby list, so use the first run after reboot when a true filesystem-cold number is required.

The initial captured results and bottleneck analysis are stored in [the 2026-07-25 baseline](performance/BASELINE-2026-07-25.md).

## Detect regressions

Add `-CompareBaseline` to run the automated PERF-006 comparison after measurement:

```powershell
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 10000 -CompareBaseline
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 50000 -CompareBaseline
```

The comparison uses versioned fixture-specific references and explicit tolerances, writes `regression.json`, and returns a failing exit code for material regressions. See [automated performance regression testing](performance/REGRESSION-TESTING.md) for the policy and baseline-update rules.
