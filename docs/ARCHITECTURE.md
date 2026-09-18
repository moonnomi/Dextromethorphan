# Architecture and production roadmap

## Project boundaries

- `Dextromethorphan.Core`: immutable media models, service contracts, queue, ReplayGain math, sleep timer, and LRC parser.
- `Dextromethorphan.Infrastructure`: WASAPI engine, direct/DSP pipelines, decoders/DoP, SQLite, JSON settings, metadata, scanner/watchers.
- `Dextromethorphan.App`: WPF composition root and presentation state.
- `Dextromethorphan.Tests`: deterministic audio, parser, queue, settings, and SQLite tests.

## Audio pipeline

```text
Decoder/DoP packer -> output-mode decision -> endpoint format probe
                    |-> Direct: byte stream -> gapless join -> event-driven WASAPI
                    `-> DSP: normalize -> rate/pitch -> per-track gain -> transition -> fades -> volume/guard -> WASAPI
```

The engine chooses the path from observable settings. Direct mode has same-format two-track preloading, exclusive-format probing, DSF/DoP framing, and device-loss recovery. DSP mode normalizes each decoder to a stable float format and performs variable-rate/pitch correction. Track-specific ReplayGain is applied before the transition mixer, which performs gapless joins or fixed/waveform-planned crossfades inside one callback stream. Session fades, software volume, double-precision accumulation, and the final clipping guard follow the mix.

## Next production increments

1. Add a bundled permissive decoder for guaranteed FLAC/ALAC/AAC/Opus behavior; add DST decompression and ASIO-native DSD.
2. Hardware-qualify exclusive negotiation, device removal, DoP payload order, and callback deadlines across USB DACs at 44.1-192 kHz and DSD64/128.
3. Replace the FFT pitch shifter with a higher-quality reviewed tempo engine and add an oversampled true-peak limiter.
4. Hardware-qualify library scanning on large SSD, SMB, and removable-drive collections; add resumable scan checkpoints.
5. Add the smart-rule and playlist editors to the UI. The typed AST, safe SQL compiler, ordered persistence, and M3U8/PLS/XSPF services are complete.
6. Expand the completed custom-chrome library shell with smart-rule, playlist, audio-profile, shortcut, and diagnostic editors.
7. Implement opt-in scrobbling and expose the existing sleep/bookmark controls in the player shell.

## Privacy

The application performs no telemetry and makes no network request by default. LRCLIB lyric lookup is independently opt-in and runs only after a manual search; requests are attributed, throttled, cached, and require confirmation before use. Future metadata, scrobble, and sync providers must follow the same explicit-consent boundary.
