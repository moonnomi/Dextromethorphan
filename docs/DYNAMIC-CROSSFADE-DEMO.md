# Dynamic crossfade demo

Research and implementation notes, 2026-09-08. Stable baseline: `bf4d907`. Demo branch: `demo/crossfade-demo`.

## Follow-up: recorded silence and visible times

Playback settings now show editable seconds beside the fade-in, fade-out and crossfade sliders. These fields apply on focus loss; Apply saves the playback draft.

**Skip trailing silence between tracks** is a separate opt-in setting, independent of dynamic crossfade. It also works in Gapless mode. The analyzer requires at least one second of peak level below -60 dBFS at the ending and keeps 200 ms of padding. Peak detection checks every channel, so a transient cannot be hidden by low window RMS. All-silent analyzed tails are preserved because the true end of the music cannot be located confidently within the 20-second window. Leading silence and the last track in a queue are preserved. This uses PCM processing and is unavailable during DSD/DoP or tempo processing.

When enabled, the effective ending is shortened in memory, and normal or dynamic overlap is calculated against that ending. Original file duration and files remain unchanged. Removing the next track restores the full ending. Late analysis keeps ordinary playback. The original research discussion below describes the initial no-trimming version; this explicit opt-in setting is the only exception.

## What is practical?

| Approach | What it can improve | Cost and limitations | Decision |
| --- | --- | --- | --- |
| Silence detection | Remove dead time at boundaries | Cannot distinguish intentional silence; trimming changes playback positions | Do not trim in this demo |
| RMS envelope matching | Adapt overlap to quiet intros and decaying outros | Cheap, explainable; amplitude is not perceived loudness or musical structure | Implement first |
| Onset/beat/phrase alignment | Align rhythmic transitions | Requires reliable tempo and phase estimates, usually longer context and time stretching; poor fit for rubato, ambient music and tempo changes | Later experiment |
| Learned DJ transitions | Jointly choose faders/EQ or other effects | Models, training assumptions, dependencies and substantial qualification | Not appropriate for the first native-player demo |

Symfonium documents waveform-based transition points and curves, but its exact selection algorithm is not public in the sources reviewed. This implementation is an independent heuristic, not a reproduction or a claim of parity. The support discussion reports early overlaps, obscured attacks and truncated tails as important failure cases. Its author cautions that amplitude estimates are approximate. [Official transitions documentation](https://docs.symfonium.app/wiki/settings/settings-playback-transitions/) · [Smart Fade discussion](https://support.symfonium.app/t/smart-fade-improvement-select-or-disable-fade-curves/12541)

FFmpeg separates silence detection (level plus minimum duration) from crossfade duration and envelope selection. That distinction matters: a single low sample or short drum gap is not a reliable outro. This demo uses sustained-window evidence, and retains its existing mixer and limiter rather than introducing FFmpeg as a runtime dependency. [FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html#silencedetect)

RMS can be computed directly from samples without an FFT. Beat tracking is a different analysis pipeline: onset strength, tempo estimation and consistent peak selection. An RMS dip alone is not evidence of a beat. [librosa RMS documentation](https://librosa.org/doc/0.10.2/generated/librosa.feature.rms.html) · [librosa beat tracking](https://librosa.org/doc/main/api/generated/librosa.beat.beat_track.html)

The supplied AI-DJ project is an offline mix-generation workflow using music analysis and planning; DJtransGAN uses learned transitions with differentiable effects and supplied cue points. Neither is a drop-in timing detector for this player's live queue. DJtransGAN provides pretrained weights, while its original training dataset is unavailable for licensing reasons. [AI-DJ repository](https://github.com/kckDeepak/AI-DJ-Mixing-System) · [DJtransGAN repository and paper reference](https://github.com/ChenPaulYu/DJtransGAN)

## Implemented heuristic

1. Decode at most the first 15 seconds and last 20 seconds of each relevant track with a separate read-only decoder. Never touch the playback decoder's position.
2. Compute nonoverlapping 50 ms windows: `20 * log10(max(channel RMS, 0.000001))`. Taking the strongest channel avoids stereo phase cancellation.
3. For each boundary, define a strong level as its 80th-percentile RMS minus 10 dB, with a -55 dBFS lower bound. These are demo tuning constants, not a published psychoacoustic standard.
4. Use a 250 ms local maximum to bridge brief gaps. Test candidate overlaps from the configured maximum downward in 50 ms increments. Accept the longest whose simultaneous strong windows occupy at most 10% of the overlap.
5. If no candidate passes, blend for 250 ms. Entirely near-silent boundaries and invalid envelopes use gapless. Cap overlap at a quarter of either track and the normal 10-second maximum.
6. Apply the chosen duration before its start deadline. Keep the selected regular curve. Finish an active fade unchanged even when settings change.

This is amplitude-based timing, not LUFS normalization, beat matching, vocal detection or content classification. Existing ReplayGain and clipping prevention remain separate. The demo does not change the gain curve automatically, and it does not remove silence. Long silent boundaries can therefore remain audible gaps. Correlated signals can still require limiter action during an overlap.

## Runtime constraints

- Off by default. Requires Crossfade mode, a positive duration and normal speed/pitch in the PCM DSP pipeline.
- Duration is the maximum; a per-output duration override remains authoritative.
- One analysis worker, a cooperative five-second request budget and at most 32 cached boundary envelopes. Cache keys include path, size, modification time and CUE boundaries. No persistent waveform database or library writes.
- Cancellation is checked between decoder reads; native decoder open/seek calls cannot be forcibly interrupted. A slow call can occupy the one worker, but does not hold the audio control gate or run in the audio callback.
- Local fixed drives only. Network/removable storage, unsupported decoders, DSD, failed/late analysis and speed/pitch processing retain regular playback behavior.
- Queue changes cancel obsolete plans; track and pipeline identities are rechecked before installing a result. Analysis never moves the next decoder or cuts the outgoing tail.
- Audio Diagnostics includes the selected duration and reason, or the fallback reason. The settings graph shows the maximum configured envelope, not the analyzed waveform.

## Try it

Launch `src/Dextromethorphan.App/bin/dynamic-crossfade-demo/Dextromethorphan.exe` with other copies closed. In Settings → Playback, enable **Dynamic crossfade — experimental demo**, choose **Crossfade**, set a maximum (try 6 seconds), and Apply. Queue at least two tracks. To audition quickly, seek to 15 seconds before the end: seeking rebuilds the pipeline, so leave more than the maximum overlap plus two seconds for the cached plan to be installed. Inspect Audio Diagnostics to see whether a plan was installed. Disable the checkbox and Apply for regular-crossfade comparison. Rebuild with `scripts/build-dynamic-crossfade-demo.ps1`.

The demo uses the usual app settings/library, but reads music files only. The stable `bin/latest` build is preserved. The new flag is inside the crossfade settings object; older builds ignore it. No automatic enabling or music-library import is performed.

## Qualification and next experiments

### Troubleshooting an inaudible crossfade

The Audio diagnostics panel now includes a 30-second, three-lane RMS history: weighted outgoing audio, weighted incoming audio, and the actual post-DSP/post-conversion PCM bytes returned to WASAPI. Readouts include submitted frames, mixed frames, RMS, peak and non-finite samples. The history uses a -60 to 0 dBFS scale, is bounded to 300 observations and updates only while visible. Levels describe the last submitted block, not an acoustic measurement; zero can mean a silent block. DSD/DoP is deliberately not interpreted as PCM.

`output-measured` log entries retain the numeric measurements once per second, and up to ten times per second during overlap. No raw audio is recorded. Export the usual diagnostic bundle after reproducing a problem. Measurements are taken before the Windows engine/driver; they do not prove physical output or capture loopback. Fixed-mode generated-signal tests verify both source contributions and the final output bytes.

Fixed a seek regression: rebuilding a PCM pipeline discarded the next prepared track. Seeking now restores that next source, allowing automatic overlap to happen after scrubbing near the ending.

Select **Crossfade** in Settings → Playback, choose a positive duration (for example 6 seconds), and **Apply**. A saved duration does not enable overlap while **Gapless** is selected. For a fixed-duration comparison, disable Dynamic crossfade. Queue at least two tracks and let playback advance naturally; the Next button deliberately skips immediately.

The `audio-transition` category in `%APPDATA%\Dextromethorphan\logs\app-*.jsonl` records settings, effective pipeline mode, next-track preparation, boundary analysis, overlap start and actual completion. `gapless-switch` means no samples were overlapped; `overlap-completed` includes the actual overlap duration. Preparation failures include their exception message. Diagnostic exports include these logs. Logging is event-based, not per audio buffer.

Optional **Skip trailing silence** is separate from adaptive overlap: it detects sustained peaks below -60 dBFS at the ending, leaves a 200 ms guard, and trims only when a next track is ready. This supersedes the initial no-trimming scope described above. Music files are never modified.

Automated cases cover loud-to-loud, quiet tail plus quiet intro, rhythmic dips, silent/invalid boundaries, short-track limits and normal crossfade continuity. Listening across genres remains necessary; this demo does not promise perceptually optimal transitions.

Next: collect actual plan outcomes, audition piano sustains and hard vocal attacks, compare adaptive timing with fixed timing using identical curves, then consider separate incoming/outgoing envelope lengths, cached waveform visualization and explicit silence handling. Beat/phrase alignment should be a separate opt-in mode with a confidence threshold and no tempo change when confidence is low.
