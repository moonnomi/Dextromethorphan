# Dynamic crossfade design and qualification

Implementation and research record for branch `demo/crossfade-demo`, updated 2026-09-18.

## What the feature means

Fixed crossfade overlaps the final configured number of seconds of one track with the beginning of the next track. Dynamic crossfade additionally analyzes both decoded waveforms and chooses:

1. an effective outgoing cue point when the file has a confidently low-energy ending;
2. an incoming cue point after sustained digital silence;
3. an overlap duration that avoids prolonged loud-on-loud collisions; and
4. the user-selected gain curves for the actual sample mix.

It is deliberately not beat matching, source separation, vocal detection, tempo synchronization, or an attempt to reproduce a private algorithm. Symfonium's public documentation says Smart Fades use waveform analysis to calculate transition points and curves, and may use volume, silence, or beats, but does not publish its implementation. Dextromethorphan therefore uses an independent, explainable waveform heuristic with conservative fallbacks.

## Source findings

- [Symfonium's official transition documentation](https://docs.symfonium.app/wiki/settings/settings-playback-transitions/) distinguishes fixed fades from waveform-based Smart Fades. It also describes separate transition points, durations, and curves. This is the behavioral reference, not source code.
- [FFmpeg's official `acrossfade`, `afade`, and `silencedetect` documentation](https://ffmpeg.org/ffmpeg-filters.html#acrossfade) treats overlap duration, the two fade curves, silence threshold, and minimum silence duration as separate parameters. Its default silence detector uses -60 dB and requires a duration, which supports using sustained windows rather than a single low sample.
- [ITU-R BS.1770-5](https://www.itu.int/rec/R-REC-BS.1770) defines programme-loudness and true-peak measurement, while [EBU R 128](https://tech.ebu.ch/publications/r128) recommends loudness normalization and maximum-level descriptors. These standards do not define crossfade timing. They do support keeping each track's normalization independent and preserving a final peak guard.

The practical consequence is that waveform timing, fade envelopes, ReplayGain, and clipping prevention are separate stages. A timing detector must not silently become a loudness standard, and ReplayGain must be applied to each source before the sources are mixed.

## Implemented pipeline

```text
outgoing decoder -> normalize -> per-track ReplayGain --\
                                                     transition mixer -> user volume -> peak guard -> WASAPI
incoming decoder -> normalize -> per-track ReplayGain --/
                         ^
                 waveform cue plan
```

Applying ReplayGain after the mixer was incorrect: when the active source changed, one track's gain could affect an entire callback containing both tracks. The transition provider now stores independent current/next gains and applies them before the fade envelopes. The final gain stage only applies software volume and clipping protection.

### Boundary analysis

- A read-only analysis decoder samples at most the first 15 seconds and last 20 seconds. It never seeks the playback decoder while analyzing and never writes to media or the library database.
- Non-overlapping 50 ms windows store the loudest channel RMS and peak. Using the strongest channel avoids stereo phase cancellation hiding content.
- Results are cached in memory for 32 track boundaries. The cache key includes path, file length, modification time, and CUE segment boundaries.
- One worker performs analysis with cancellation and a five-second request budget. Local fixed drives and seekable PCM are supported. DSD, network/removable storage, tempo processing, failed analysis, and late plans retain fixed crossfade.

### Cue selection

The constants below are tuning rules, not claims from ITU, EBU, FFmpeg, or Symfonium.

Outgoing smart cue:

- Calculate the 80th-percentile RMS of the analyzed tail as a local reference.
- Treat a suffix as low energy only after both RMS falls below `max(-50 dBFS, reference - 30 dB)` and peak falls below `max(-45 dBFS, reference - 25 dB)`.
- Require at least 1.5 seconds of that suffix, preserve 500 ms after the last detected content window, and advance by at most 10 seconds.
- If the whole analyzed tail is quiet, preserve it: there is no trustworthy content boundary inside the window.

Incoming smart cue:

- Require at least 500 ms of consecutive peak level below -60 dBFS at the file start.
- Keep 100 ms of preroll before the first detected attack and skip at most 10 seconds.
- An all-silent analyzed head is preserved because no attack was found.
- Seeking is installed only before any incoming samples have been consumed. Disabling/replanning dynamic mode restores the incoming decoder to zero.

The separate **Skip trailing silence** option remains stricter: it needs at least one second below -60 dBFS and retains 200 ms. It works without adaptive duration and never changes files.

### Overlap selection and rendering

- Cap the overlap by the configured maximum, ten seconds, the analyzed windows, and one quarter of either effective track duration.
- Derive a strong-content threshold per boundary from its 80th-percentile RMS minus 10 dB, bounded at -55 dBFS.
- Use a 250 ms local maximum so brief drum gaps do not appear to be an outro.
- Search from the maximum duration downward in 50 ms steps. Accept the longest candidate where no more than 10% of windows contain strong content from both tracks.
- If both boundaries remain strong, use a short 250 ms blend. Invalid or near-silent envelopes fall back to gapless/fixed behavior without throwing into playback.
- The mixer renders both sources in the same audio callback with linear, equal-power, smoothstep, or custom curves. Active plans and curves are immutable until that overlap finishes.

This avoids the earlier false-positive state where samples technically overlapped but the incoming track was still silent or the outgoing music had already ended.

## Diagnostics

Audio Diagnostics exposes a bounded 30-second graph and numeric measurements for:

- weighted outgoing RMS;
- weighted incoming RMS;
- mixed frame count and overlap state;
- post-DSP PCM RMS/peak actually submitted to WASAPI; and
- non-finite sample count.

The `audio-transition` log category records `next-ready`, boundary-plan details, `overlap-started`, `overlap-completed` or `gapless-switch`, and periodic `output-measured` events. These measurements are immediately before the Windows audio engine/driver. They prove what Dextromethorphan submitted, not what an analog DAC emitted.

Enable Settings > Diagnostics > Debug mode for track context actions such as opening the file location, copying the path, probing the decoder, and exporting diagnostics. No raw audio is recorded.

## Reproducible qualification

Run the complete deterministic suite:

```powershell
dotnet test .\Dextromethorphan.slnx -c Release --no-restore
```

Run only waveform planning and sample-rendering qualification:

```powershell
dotnet test .\tests\Dextromethorphan.Tests\Dextromethorphan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DynamicCrossfadeTests|FullyQualifiedName~BoundaryEnvelopeAnalyzerTests|FullyQualifiedName~DspQualificationTests"
```

Run short generated-signal probes through the default Windows endpoint:

```powershell
$env:DEXTROMETHORPHAN_RUN_CROSSFADE_TEST = '1'
dotnet test .\tests\Dextromethorphan.Tests\Dextromethorphan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CrossfadeOutputHardwareTests"
```

The endpoint gate creates temporary 48 kHz stereo float WAV files and deletes them afterward. It validates:

- fixed two-second overlap survives a seek and reaches WASAPI;
- dynamic cue analysis advances a quiet outgoing suffix and incoming digital silence;
- the source transition reports a real overlap;
- both source contributions produce mixed frames;
- PCM frames reach the output tap; and
- output contains no NaN or infinity.

Qualification result on 2026-09-18:

| Gate | Result |
| --- | --- |
| Fixed WASAPI generated-signal smoke | Passed, 4 s |
| Dynamic WASAPI generated-signal smoke | Passed, 3 s |
| Full Release suite | Passed, 514/514 |
| Isolated-AppData demo startup/clean close | Passed, healthy after 5 s, exit code 0 |
| Media files or library modified | No |

Build the isolated demo without replacing `bin/latest`:

```powershell
.\scripts\build-dynamic-crossfade-demo.ps1
```

Launch `src/Dextromethorphan.App/bin/dynamic-crossfade-demo/Dextromethorphan.exe`, select Crossfade, choose a maximum duration and curve, enable Dynamic crossfade, and Apply. Queue at least two tracks and let playback advance naturally. The Next button intentionally performs an immediate skip rather than a crossfade.

## Known limits and next research

- Waveform energy cannot identify vocals, bars, phrases, key, or artistic intent. Some quiet endings and intentional silence will remain imperfect.
- Cue advancement is enabled only by Dynamic crossfade (or the explicit trailing-silence option). Fixed crossfade always retains file boundaries.
- Beat alignment requires onset detection, tempo/phase confidence, and potentially time stretching. It should be a separate opt-in experiment with a no-change fallback, not folded into this amplitude heuristic.
- Listening qualification across piano sustains, live recordings, hard vocal attacks, ambient fades, and mastered loudness extremes is still valuable even though the generated-signal and sample-level gates pass.
