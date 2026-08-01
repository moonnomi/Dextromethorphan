# Milestone 3 audio status — 2026-07-31

The release-candidate software gate is complete. The self-contained win-x64 build passed the full release pipeline and is available at `src/Dextromethorphan.App/bin/latest/Dextromethorphan.exe`; the distributable archive is `artifacts/Dextromethorphan-win-x64.zip`.

## Completed in this milestone

- Per-device WASAPI profiles, exclusive capability discovery, explicit fallback reporting, endpoint recovery, retry/backoff, and diagnostics.
- Legal generated decoder coverage for FLAC, MP3, WAV, AIFF, Ogg Vorbis, Opus, AAC/M4A, ALAC, WMA, DSF, and uncompressed DFF, plus read-only external bit-exact qualification for DST-compressed DFF, including malformed/truncated inputs and seek/end behavior.
- CUE single-image albums and embedded chapters.
- Callback-continuous gapless playback and equal-power crossfade qualification.
- EBU R128-compatible ReplayGain analysis with album/track controls, sample-peak clipping prevention, cancellation, and playback-aware throttling.
- SoundTouch tempo/pitch processing with output-frame timeline, lyric, SMTC, and latency alignment.
- The complete 36-case PCM conversion matrix plus real shared/exclusive endpoint playback and buffer-boundary qualification.
- A repeatable, device-selectable physical-DAC qualification script and manual matrix.
- An isolated long-play runner that alternates generated 44.1/48 kHz PCM through the real event-driven transition pipeline and records callback deadlines, recovery, CPU, memory, and endpoint-volume invariance.

The current release test suite passes 276 automated tests. The opt-in onboard-device gate separately passed 36/36 shared formats, 24/24 driver-supported exclusive formats, and 2/10/100 ms buffers with endpoint volume unchanged. The external DST gate separately decoded three consecutive compressed frames bit-for-bit and verified cross-frame DoP seeking without modifying its source fixtures. Generated DSD64/128 tests verify marker order, two- and six-channel interleave, odd callback alignment, and seeking for DSF, uncompressed DFF, and DST-contained DSD. The physical DoP runner additionally generates finite DSD64/128 DSF silence, requires a pinned non-default endpoint and traceable DAC metadata, exercises direct exclusive playback and repeated seeks, and treats missing DAC-display evidence as non-qualifying. It has not been sent to the current onboard endpoint. The hardened schema-2 soak smoke test completed 20.008239 observed playing seconds and seven mixed-rate crossfaded transitions with no excluded gaps, non-playing time, callback deadline misses, or recovery attempts; peak/final working-set growth was 6,893,568/1,736,704 bytes and endpoint volume was bit-identical before/after. This validates the runner, not the eight-hour acceptance gate.

## Data-safety boundary

Format fixtures are generated or legally bundled test assets. ReplayGain safety tests analyze copied audio and verify source hashes/timestamps. The hardware gate emits only finite in-memory silence. The soak runner creates PCM silence under the Windows temporary directory and deletes it when finished; its project contains no library, scanner, database, or settings service. It uses fixed output volume and only reads the endpoint scalar before and after the run. None of these qualification paths opens the live music library, artwork cache, or library database, and the release build does not launch the application. Read-only post-build hashes confirmed the existing live database and settings were not modified.

## Still open

| Item | Why it remains open | Closure condition |
|---|---|---|
| DEC-008: ASIO/native DSD evaluation | P3 decision intentionally follows physical WASAPI/DoP evidence | Complete the DAC/DoP matrix, then record a keep/defer/adopt decision |
| HW-003: physical DoP | Generated framing/seeking, carrier discovery, and guarded runner safety tests pass, but no compatible DAC is currently available | Retain a schema-2 physical report with direct 176.4/352.8 kHz negotiation, repeated seeks, unchanged endpoint volume, traceable device metadata, and DAC DSD64/128 indication |
| HW-004: long soak | The isolated runner and short live smoke test pass; eight real elapsed hours have not yet completed | Retain an eight-hour diagnostics report with accepted callback-deadline, recovery, volume, and memory results |

These are not silently treated as passing. Until HW-003 is complete, diagnostics may describe generated DoP framing but must not claim that a physical DAC received native DSD correctly.
