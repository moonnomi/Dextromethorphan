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

Here, an `Underruns` count is specifically a provider callback deadline miss: producing a requested audio buffer took longer than that buffer's playback duration. It is an actionable software-path timing signal, not a driver- or DAC-reported USB underrun counter.

The runner advances its acceptance clock only across frequently observed `Playing` intervals. A loop gap longer than one second—such as system sleep, debugger suspension, or severe process starvation—is recorded as an unobserved gap and does not count toward eight playback hours. Five accumulated observed minutes outside `Playing` is a failure. Callback deadline misses and maximum callback time are cumulative across endpoint recovery/rebuilds, so reopening the pipeline cannot erase an earlier defect. The memory limit applies to peak growth, not merely the final value after garbage collection.

## Run it

The default command is the actual eight-hour gate:

```powershell
./scripts/Start-AudioSoak.ps1
```

For a particular endpoint or an explicit report path:

```powershell
./scripts/Start-AudioSoak.ps1 `
  -DeviceId '<Windows endpoint ID>' `
  -OutputPath 'artifacts/audio-soak/eight-hour.json'
```

Pressing Ctrl+C records a non-qualifying cancelled report instead of treating a partial run as success.

## Runner smoke qualification

The hardened schema-2 runner was qualified for 20 observed playing seconds on the current Realtek shared endpoint using three-second generated tracks and a 0.25-second crossfade:

| Signal | Result |
|---|---:|
| Requested / observed playing time | 20 / 20.008239 seconds |
| Wall / excluded gap / non-playing time | 20.0592302 / 0 / 0 seconds |
| Completed mixed-rate transitions | 7 |
| Premature playback ends | 0 |
| Callback deadline misses | 0 |
| Endpoint recovery attempts | 0 |
| Maximum provider callback | 6.3457 ms |
| Initial / peak / final working set | 37,683,200 / 44,576,768 / 39,419,904 bytes |
| Peak / final working-set growth | 6,893,568 / 1,736,704 bytes |
| Average normalized CPU | 0.1461% |
| Endpoint volume before / after | 0.7016084 / 0.7016084 |

This short result validates the hardened harness and safety invariants only. **HW-004 remains open** until a schema-2 report shows at least eight observed `Playing` hours, no faults or premature ends, zero cumulative deadline misses/recovery attempts, unchanged endpoint volume, and accepted bounded peak memory growth.
