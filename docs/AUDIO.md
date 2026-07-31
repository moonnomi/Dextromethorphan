# Audio engine

## Modes

| Feature | Direct pipeline | DSP pipeline |
|---|---:|---:|
| Exclusive event-driven WASAPI | Yes | Yes when the float format is accepted |
| Shared-mode fallback | Configurable | Configurable |
| Bit-perfect diagnostic | Only exclusive, unprocessed direct mode | Never |
| Same-format gapless | Byte-continuous | Sample-continuous |
| Different-rate gapless | Reopens the endpoint | Normalized and sample-continuous |
| Crossfade | No | Equal-power, 0-10 seconds |
| ReplayGain/preamp | No | Track/album, tagged-peak clipping prevention |
| Software volume | No | Yes |
| Hardware endpoint volume | Yes | Yes |
| Speed/pitch | No | 0.5x-1.5x, +/-12 semitones, optional pitch preservation |
| DSF/DFF DoP | Yes | Rejected to protect the DSD payload |

`PlaybackSnapshot.Diagnostics` reports the pipeline, requested/effective WASAPI mode, source/output formats, decoder, bit-perfect state, and reason. An exclusive request can fail or fall back to shared according to the profile; fallback is never silent in diagnostics.

## Transitions

The next track is decoded before the current one ends. With crossfade disabled, one callback can read the final samples of track A and the first samples of track B, so the endpoint is not stopped between compatible tracks. Crossfade uses sine/cosine equal-power curves. A differing rate/channel count is normalized before entering the transition provider.

Direct mode performs the callback-spanning join without converting samples, but only when both decoded `WaveFormat` values are identical. A differing format requires an endpoint reopen because exclusive WASAPI cannot change format inside an active stream.

## ReplayGain and volume

ReplayGain fields are read from Vorbis comments, ID3v2 TXXX frames, and APE tags. Opus `R128_TRACK_GAIN`/`R128_ALBUM_GAIN` Q7.8 values are accepted when ReplayGain tags are absent. Album mode falls back to track gain. Preamp is added in dB; with clipping prevention, `REPLAYGAIN_TRACK_PEAK` constrains gain before playback. Gain and crossfade accumulation use `double`, then emit float samples through a final `[-1, 1]` guard.

Any software gain is non-bit-perfect. To retain direct playback below full volume, endpoint/hardware volume must be enabled for that device profile.

Normal playback never assigns `WasapiOut.Volume`: NAudio maps that property to Windows endpoint volume, so doing so during a pipeline rebuild can overwrite the user's system level. Dextromethorphan's slider controls the internal gain stage. The Windows endpoint is written only when the selected output profile explicitly enables `HardwareVolume`.

## Speed and pitch

The DSP graph uses SoundTouch.Net 2.3.2 in high-quality mode: quick seek is disabled, the anti-alias filter is enabled, and its filter length is increased to 64 taps. Tempo and pitch are independent when pitch preservation is enabled. With preservation disabled, speed changes pitch naturally and the semitone control remains an additional shift.

SoundTouch runs per track before the gapless/crossfade provider, so crossfade duration remains measured in real output seconds. The media clock is derived from frames actually handed to the output multiplied by playback speed, rather than from decoded input buffered ahead by the processor. Diagnostics expose the processor, its reported average latency, and the timeline clock. Unity speed/pitch bypasses SoundTouch entirely.

## DSD over PCM

DSF and both uncompressed and DST-compressed DFF/DSDIFF are streamed block-by-block as DoP 1.1. DST is decompressed losslessly to native channel-interleaved DSD one 1/75-second frame at a time, then enters the same DoP framer; it is never converted to PCM. Every 16 DSD bits occupy the lower bytes of a 24-bit frame; the most-significant byte alternates `0x05` and `0xFA` across all channels. DSD64 negotiates 176.4 kHz/24-bit PCM. Seeking decodes only the containing DST frame and resumes at the requested DoP frame.

DoP requires an exclusive DAC and `DsdMode: "Dop"`. ReplayGain, crossfade, fades, software volume, and speed/pitch are rejected rather than corrupting DSD. DST compression and ASIO-native DSD are pending.

## Decoder coverage

- Managed/built-in: WAV, AIFF, MP3, FLAC, Ogg Vorbis, Opus, DSF-to-DoP, and uncompressed or DST-compressed DFF-to-DoP on win-x64.
- Windows Media Foundation with startup capability probes: AAC/M4A, ALAC, WMA, and other installed transforms.

## Verification

Automated tests cover callback-spanning gapless joins, crossfade output, SoundTouch tempo/pitch frequency and duration behavior, measured processing displacement, ReplayGain analysis and peak math, clipping guard behavior, sleep-at-end state, exact DSF/DoP payload/marker framing, DST container validation, malformed-frame containment, and bit-exact external DST qualification. USB-driver exclusive behavior and DAC interpretation require a physical hardware matrix.
