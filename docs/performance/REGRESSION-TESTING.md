# Automated performance regression testing

PERF-006 compares each standard benchmark summary with a versioned reference captured on the designated Windows performance machine. It complements the absolute [release gates](RELEASE-GATES.md):

- release gates answer “is this fast enough to qualify?”;
- regression comparison answers “did this change make established behavior materially worse?”

A result can pass regression comparison while still failing an absolute release gate. The stored baseline therefore must not be interpreted as a consumer-readiness target.

## Reference set

The tracked references were captured at revision `cb1192a` with four fresh Release processes per fixture:

- [10k Windows x64 reference](baselines/reference-10k-windows-x64.json)
- [50k Windows x64 reference](baselines/reference-50k-windows-x64.json)

Both references use fixture seed `20260725` and record the fixture content hash. Comparison refuses a different fixture hash, fewer than four runs, or a different processor/architecture by default.

## Run and compare in one command

```powershell
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 10000 -CompareBaseline
.\scripts\Measure-PerformanceBaseline.ps1 -Tracks 50000 -CompareBaseline
```

Each run writes the normal benchmark output plus `regression.json`. The command exits with code `1` when any tracked metric exceeds its tolerance.

An existing four-run summary can be checked without rerunning the app:

```powershell
.\scripts\Compare-PerformanceBaseline.ps1 `
  -Summary .\performance-results\<run>\summary.json
```

Use `-ReportOnly` for exploratory work that should print and serialize regressions without failing the shell. `-AllowMachineMismatch` is intentionally explicit and should never be used for a gating release result.

## Tolerance policy

The machine-readable policy is [regression-policy.json](regression-policy.json). It tracks startup, first artwork, overall and per-view cached navigation, scrolling, memory, idle/playback CPU, and scan throughput.

For every metric, the allowed tolerance is the larger of:

- the configured percentage of the reference value; or
- the configured absolute noise floor.

Lower-is-better metrics fail above `reference + tolerance`. Scan throughput is higher-is-better and fails below `reference - tolerance`. Frames over 50 ms have zero tolerance.

This dual threshold avoids flagging tiny absolute changes in small metrics while still catching proportionally large regressions. Tolerances are reviewable data, not embedded constants in the comparison script.

Cold startup and scan throughput deliberately use wider bands than warm UI metrics because Windows standby-cache, JIT, filesystem, and antivirus state create substantially more run-to-run variance. Absolute release gates still enforce the product requirements independently; the wider regression band does not waive them.

## Updating a reference

Only update a stored reference after an intentional, measured change:

1. Build the exact revision to become the new reference.
2. Run the default four-process 10k and 50k benchmarks with the window visible and unobstructed.
3. Confirm the fixture hashes and machine identity match.
4. Run both the regression comparator and absolute release gates.
5. Replace the relevant reference JSON and document why the old result is no longer representative.

Never refresh a baseline merely to make a regression disappear.
