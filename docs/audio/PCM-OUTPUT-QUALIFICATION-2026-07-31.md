# PCM output qualification — 2026-07-31

This report closes AUD-006 and records the current-release HW-001 check. It combines deterministic provider tests with opt-in playback against a real Windows endpoint.

## Safety boundary

The hardware test creates finite in-memory silence. It does not enumerate, decode, retag, rename, move, or write music files; it does not open the library database or artwork cache; and it never assigns Windows endpoint volume. The test compares the endpoint volume value before and after playback and fails if it changes.

## Deterministic PCM matrix

The provider test covers every combination of:

- 44.1, 48, 88.2, 96, 176.4, and 192 kHz;
- 16, 24, and 32-bit integer PCM;
- mono and stereo;
- callback sizes smaller than one frame and sizes that are not frame-aligned.

All 36 combinations preserve block alignment, return the exact expected byte count, and terminate cleanly. This also guards the custom 24-bit conversion path used by fixed output profiles.

## Real endpoint playback

The opt-in test ran on `System default — Speakers (Realtek(R) Audio)`, whose shared mix format was 48 kHz, 32-bit extensible stereo.

- Shared event-driven WASAPI: 36/36 rate, depth, and channel combinations played finite silence successfully.
- Exclusive event-driven WASAPI: 24/24 driver-supported combinations played finite silence successfully. Unsupported 32-bit exclusive formats remain correctly excluded by capability negotiation rather than attempted as if supported.
- Buffer boundaries: 2 ms, 10 ms, and 100 ms shared-mode buffers all completed successfully.
- Endpoint volume: unchanged after the entire matrix.

The test writes its JSON evidence before assertions so a failing driver row remains available for diagnosis. Run it explicitly with:

```powershell
$env:DEXTROMETHORPHAN_RUN_AUDIO_HARDWARE_TESTS = '1'
$env:DEXTROMETHORPHAN_AUDIO_REPORT = (Join-Path $PWD 'artifacts/audio-qualification.json')
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj --filter FullyQualifiedName~AudioHardwareQualificationTests
```

## Remaining hardware gates

This is not evidence for a physical USB DAC, native DSD, DoP indication, hot-unplug behavior on a DAC, or an eight-hour soak. Those remain explicitly tracked by HW-002 through HW-004 and cannot be honestly closed without the relevant hardware and elapsed run time.
