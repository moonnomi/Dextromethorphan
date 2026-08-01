# Audio soak qualification — 2026-07-31

This gate exercises the real `WasapiAudioEngine`, decoder, sample-rate normalization, event-driven callbacks, crossfade transition pipeline, and endpoint lifecycle for a requested elapsed duration. It is designed to make HW-004 repeatable without reading or changing the user's music library.

## Safety boundary

- The runner generates two silent stereo 16-bit WAV files (44.1 and 48 kHz) in a new Windows temporary directory.
- It does not reference the app, library/scanner services, SQLite, settings, artwork cache, or any user music path.
- It selects `VolumeControlMode.Fixed`, disables legacy hardware volume, and never calls the endpoint volume setter.
- It reads the endpoint volume scalar before and after playback and requires bit-for-bit equality.
- It deletes its generated fixture directory on exit. Reports redact the endpoint ID and contain no source paths.
- Shared-mode output is deliberate for the unattended run so it does not monopolize the device. Physical exclusive/DoP qualification remains a separate gate.

## Recorded signals

The JSON report contains:

- requested observed-playing duration, wall duration, excluded sleep/stall gaps, and non-playing time;
- premature playback ends, fault messages, transitions, and recovery attempts;
- callback read timing and callback deadline misses;
- initial, peak, and final process working set plus final and peak growth;
- process CPU time and average normalized CPU percentage;
- periodic playback state and memory samples; and
- endpoint volume before/after and an unchanged flag.

Schema 3 writes the report atomically at every sample interval. A terminated host therefore leaves a valid `Running` checkpoint rather than no evidence or a half-written JSON file. Each checkpoint includes the runner PID, resolved endpoint-ID hash, observed playback clock, cumulative diagnostics, volume observations, and memory samples. The final report distinguishes `RunPassed` (the requested duration passed) from `Qualified` (at least eight requested and observed playing hours passed).

Here, an `Underruns` count is specifically a provider callback deadline miss: producing a requested audio buffer took longer than that buffer's playback duration. It is an actionable software-path timing signal, not a driver- or DAC-reported USB underrun counter.

The runner advances its acceptance clock only across frequently observed `Playing` intervals. A loop gap longer than one second—such as system sleep, debugger suspension, or severe process starvation—is recorded as an unobserved gap and does not count toward eight playback hours. Five accumulated observed minutes outside `Playing` is a failure. Callback deadline misses and maximum callback time are cumulative across endpoint recovery/rebuilds, so reopening the pipeline cannot erase an earlier defect. The memory limit applies to peak growth, not merely the final value after garbage collection.

## Run it

The foreground command is the actual eight-hour gate:

```powershell
./scripts/Start-AudioSoak.ps1
```

For an unattended run, use the detached Task Scheduler launcher. It publishes an immutable runner copy, executes under the current interactive user so WASAPI remains available, survives terminal/Codex process teardown, permits battery operation, wakes when possible, and checkpoints the report every 30 seconds:

```powershell
./scripts/Start-AudioSoak.ps1 `
  -Detached `
  -OutputPath 'artifacts/audio-soak/eight-hour-schema3.json'

./scripts/Get-AudioSoakStatus.ps1 `
  -OutputPath 'artifacts/audio-soak/eight-hour-schema3.json'
```

After completion, the strict milestone validator must pass:

```powershell
./scripts/Test-AudioSoakReport.ps1 `
  -ReportPath 'artifacts/audio-soak/eight-hour-schema3.json'

./scripts/Get-AudioSoakStatus.ps1 `
  -OutputPath 'artifacts/audio-soak/eight-hour-schema3.json' `
  -CleanupCompletedTask
```

For a particular endpoint or an explicit report path:

```powershell
./scripts/Start-AudioSoak.ps1 `
  -DeviceId '<Windows endpoint ID>' `
  -OutputPath 'artifacts/audio-soak/eight-hour.json'
```

Pressing Ctrl+C during a foreground run records a non-qualifying cancelled report instead of treating a partial run as success. A detached task retains a non-qualifying `Running` checkpoint if its host is externally terminated.

## Runner smoke qualification

The schema-3 runner and detached launcher were qualified for 20 observed playing seconds on the current Realtek shared endpoint using three-second generated tracks, a 0.25-second crossfade, and two-second atomic checkpoints. The launcher command returned while Task Scheduler kept the runner alive; the final task result and runner exit code were both zero:

| Signal | Result |
|---|---:|
| Requested / observed playing time | 20 / 20.0297687 seconds |
| Wall / excluded gap / non-playing time | 20.0774791 / 0 / 0 seconds |
| Completed mixed-rate transitions | 7 |
| Premature playback ends | 0 |
| Callback deadline misses | 0 |
| Endpoint recovery attempts | 0 |
| Maximum provider callback | 5.9626 ms |
| Initial / peak / final working set | 37,658,624 / 52,862,976 / 48,304,128 bytes |
| Peak / final working-set growth | 15,204,352 / 10,645,504 bytes |
| Average normalized CPU | 0.2821% |
| Endpoint volume before / after | 0.060000002 / 0.060000002 |
| Volume changed at any checkpoint | No |
| Requested run / eight-hour gate | Passed / not qualified (expected) |

An earlier schema-2 attempt ran cleanly for 38 observed minutes and 304 transitions, then its parent host vanished without a final report. It is not accepted as soak evidence. Atomic checkpoints and detached scheduling directly address that harness failure.

This short result validates the hardened harness and safety invariants only. **HW-004 remains open** until a schema-3 report shows at least eight requested and observed `Playing` hours, no faults or premature ends, zero cumulative deadline misses/recovery attempts, unchanged endpoint volume throughout, no more than 128 MiB peak working-set growth, and passes `Test-AudioSoakReport.ps1`.
