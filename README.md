# Dextromethorphan

Dextromethorphan is an offline-first native Windows music player inspired by Symfonium's depth and desktop audio workflows. The repository contains a buildable WPF/.NET 10 application with a persistent library, real-time scanner, synced lyrics, queue, and event-driven WASAPI audio engine.

## Run

Requirements: Windows 10 2004 or later, the .NET 10 SDK, and an audio endpoint.

```powershell
dotnet restore Dextromethorphan.slnx
dotnet run --project src/Dextromethorphan.App
```

On first launch, choose **Add music folder**. Local folders, mounted drives, UNC/SMB paths, and network drives use the same asynchronous scanner. App data is stored in `%APPDATA%\Dextromethorphan`.

## Implemented

- Event-driven WASAPI with endpoint probing, shared/exclusive profiles, exact-format fallback policy, configurable buffers, device recovery, and direct/DSP diagnostics.
- Byte-preserving direct playback with same-format gapless preloading.
- Normalized DSP playback with cross-format gapless transitions, 0-10 second equal-power crossfade, ReplayGain, tagged-peak clipping prevention, fades, software volume, and pitch-preserving speed from 0.5x to 1.5x.
- Native DSF-to-DoP 1.1 streaming. The one-bit DSD payload is framed, not converted to PCM.
- SQLite WAL library with transactional scan batches, FTS5 prefix/diacritic search, ratings/love/play state, bookmarks, manual and rule-based smart playlists, and atomic JSON settings.
- Recursive concurrent scanning with a bounded database writer and debounced file watchers. Tags, embedded lyrics, `.lrc`, and `.txt` sidecars are supported; embedded artwork is held in a deterministic, size-limited on-disk cache instead of bloating the library database.
- Ordered playlist persistence plus UTF-8 M3U8, PLS v2, and XSPF import/export.
- Rebindable in-app and system-wide shortcuts with conflict reporting, media-key routing, and Windows System Media Transport Controls for play/pause/next/previous/stop, seeking, timeline state, and track metadata.
- Managed Ogg Vorbis decoding; WAV, AIFF, MP3, and Media Foundation decoder paths.
- Queue replace/play-next/add/move/remove, repeat/shuffle, 50-level undo/redo, automatic next-track preloading, sleep-timer backend, and bookmark resume/save.
- LRC and enhanced-LRC parsing with millisecond, multi-timestamp, line, and word timing.
- Native custom-chrome WPF shell with functional Albums, Artists, Genres, Songs, Folders, Playlists, Favorites, and Now Playing views; cached artwork cards; a lightweight virtualized track list; persistent transport controls; and a collapsible queue inspector.

## Audio-mode contract

"Bit-perfect" and DSP are separate modes. Exclusive WASAPI with event sync and no DSP avoids the Windows shared-mode mixer and resampler. Software volume, ReplayGain, crossfade, fades, or speed/pitch processing deliberately selects the DSP path and is reported as non-bit-perfect. Hardware volume can retain the direct path, but diagnostics conservatively do not certify the final DAC result as bit-perfect.

Direct gapless playback is byte-continuous when adjacent decoded formats match. The DSP path normalizes differing sample rates for sample-continuous transitions. FLAC/AAC/ALAC/M4A/WMA/Opus currently use installed Windows Media Foundation codecs. Ogg Vorbis uses bundled NVorbis. Uncompressed DSF and DFF support DoP; DST compression and native-ASIO DSD remain pending.

See [docs/AUDIO.md](docs/AUDIO.md) for the mode matrix, [docs/LIBRARY.md](docs/LIBRARY.md) for library and playlist behavior, [docs/UI.md](docs/UI.md) for navigation and shell behavior, [docs/WINDOWS-INTEGRATION.md](docs/WINDOWS-INTEGRATION.md) for shortcuts and media controls, and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for subsystem boundaries.

## Test and package

```powershell
dotnet test Dextromethorphan.slnx
.\scripts\build-release.ps1 -Runtime win-x64 -SelfContained
# With Inno Setup 6 installed:
.\scripts\build-release.ps1 -Runtime win-x64 -SelfContained -Installer
```

## License notes

NAudio and NVorbis are MIT, Microsoft.Data.Sqlite is MIT, SQLitePCLRaw is Apache-2.0, and TagLibSharp is LGPL-2.1. Replace `ITrackMetadataReader` if a permissive-only distribution policy is mandatory.
