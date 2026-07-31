# Tempo, pitch, and timeline qualification

Date: 2026-07-31

This report is the software evidence for DSP-005 and DSP-007.

## Engine decision

The linear interpolator plus NAudio FFT pitch shifter has been removed from
the production path. It is replaced by
[SoundTouch.Net 2.3.2](https://www.nuget.org/packages/SoundTouch.Net/), a
managed C# port of the established SoundTouch time-stretch and pitch engine.
The selected mode disables quick seek, enables anti-aliasing, and uses a
64-tap anti-alias filter.

SoundTouch independently controls tempo, rate, and semitone pitch. This maps
directly to the app's 0.5x-1.5x speed, +/-12 semitone pitch, and preserve-pitch
controls without inverse correction through two lower-quality processors.
Unity speed and pitch bypass SoundTouch, so ReplayGain-only playback does not
pay its latency or CPU cost.

The package is LGPL-2.1-or-later. Its exact license is copied from the restored
NuGet package into every build and publish output as
`licenses/SoundTouch.Net-LGPL-2.1.txt`. The package source and pinned version
are documented, and a NuGet vulnerability audit reported no known vulnerable
direct or transitive packages on 2026-07-31.

## Graph and timeline alignment

Each normalized track is tempo/pitch processed before it reaches the
gapless/crossfade provider. Consequently:

- crossfade duration remains measured in output seconds at every speed;
- track-change events occur at the output boundary instead of when SoundTouch
  buffers the next input;
- seeking rebuilds the graph at the current media position;
- the playback position is derived from frames presented to WASAPI multiplied
  by media speed, not from decoded input consumed ahead;
- lyrics and SMTC continue to consume the same `PlaybackSnapshot.Position`.

SoundTouch's reported initial and nominal-output frame counts are converted to
an average processing-latency measurement. The diagnostics panel and exported
diagnostic model expose processor name, latency in milliseconds, and the clock
source.

## Objective qualification

Generated mono floating-point fixtures verify:

- 997 Hz remains within 985-1010 Hz at 1.25x with pitch preservation;
- +12 semitones moves 440 Hz into the expected 860-900 Hz band without
  changing output duration;
- both configured speed limits, 0.5x and 1.5x, produce the exact target frame
  count while retaining the original pitch band;
- an impulse's measured media-timeline displacement stays within the
  processor-reported latency plus a 10 ms tolerance;
- reported initial latency is non-zero and below 200 ms, while the average
  latency exposed to diagnostics is below 100 ms in the qualified 48 kHz mode.

```powershell
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter FullyQualifiedName~AudioPipelineTests.SoundTouch
dotnet list src/Dextromethorphan.Infrastructure/Dextromethorphan.Infrastructure.csproj `
  package --vulnerable --include-transitive
```

Focused result on 2026-07-31: 5/5 tempo, pitch, duration, boundary-speed, and
timeline cases passed. Final subjective comparison across the user's future
DAC/headphone matrix remains separately tracked by HW-004; this report does
not claim hardware listening qualification.
