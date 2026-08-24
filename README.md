<p align="center">
  <img src="docs/images/dextromethorphan-logo.png" alt="Dextromethorphan Music Player" width="360">
</p>

<p align="center">
  A fast, offline-first music player built for Windows.<br>
  Native playback, a focused library, synced lyrics, and no telemetry.
</p>

<p align="center">
  <strong>Windows 10/11</strong> · <strong>WPF</strong> · <strong>.NET 10</strong> · <strong>SQLite</strong> · <strong>WASAPI</strong>
</p>

> [!NOTE]
> Dextromethorphan is under active development. The core player is usable, but settings, audio-device behavior, and parts of the interface may still change.

## What it looks like

### Your library

Square artwork, quick navigation, a persistent player, and an editable queue—without a spreadsheet-style track grid.

![Dextromethorphan album library](docs/images/library.png)

### Collections and queue

Open any album, artist, genre, or folder in its own tab, then play individual tracks or send the full collection to the queue.

![Dextromethorphan collection view and playback queue](docs/images/collection-and-queue.png)

## Highlights

- **Windows-native audio** — event-driven WASAPI shared and exclusive modes, per-device profiles, gapless playback, crossfade, ReplayGain, speed control, and DSD over PCM (DoP).
- **Offline library** — local folders, mounted drives, and SMB/UNC paths are scanned into a fast SQLite library with per-source controls, guarded scheduled scans, file watching, and cached artwork.
- **Flexible browsing** — albums, artists, genres, songs, a hierarchical folder tree, playlists, favorites, and fast full-library search.
- **Modern playback flow** — temporary queue, visible drag reordering, history-aware Previous, undo/redo, shuffle, repeat, bookmarks, and persistent stop-after controls.
- **Lyrics that belong in the player** — static, LRC, and enhanced-LRC lyrics with line/word timing, smooth centered scrolling, click-to-seek, editing, per-track offsets, and an optional manual LRCLIB lookup.
- **Settings without JSON editing** — live Dark, Light, and AMOLED themes, accessible accents, staged playback and metadata changes, per-view layouts, shortcut editing, import/export, diagnostics, and local-data recovery.
- **Desktop integration** — rebindable shortcuts, media keys, Windows media controls, session restore, and an audio diagnostics panel.

## Run it from source

You need Windows 10 version 2004 or newer and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/moonnomi/Dextromethorphan.git
cd Dextromethorphan
dotnet restore Dextromethorphan.slnx
dotnet run --project src/Dextromethorphan.App
```

On first launch, select **Add music folder**. Your library, settings, and cache live in `%APPDATA%\Dextromethorphan`.

For a self-contained setup, launch with `--portable` or place `portable.mode` beside the executable. See [Portable mode](docs/PORTABLE-MODE.md).

## Audio support

Dextromethorphan has separate direct and DSP playback paths. Exclusive WASAPI without DSP can bypass the Windows shared mixer; enabling software volume, ReplayGain, crossfade, fades, or speed processing intentionally uses the DSP path instead.

Managed decoders handle FLAC and Ogg Vorbis. WAV, AIFF, and MP3 use native NAudio paths, while AAC, ALAC, M4A, and WMA use Windows Media Foundation where a platform decoder is available; Opus uses the bundled Concentus path. DSF plus uncompressed or DST-compressed DFF can be streamed as DoP to compatible hardware.

For the exact mode and fallback rules, see [Audio](docs/AUDIO.md).

## Build and test

```powershell
dotnet test Dextromethorphan.slnx
./scripts/build-release.ps1 -Runtime win-x64 -SelfContained
```

Add `-Installer` when [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed.

## Documentation

- [Audio engine and playback modes](docs/AUDIO.md)
- [Milestone 3 audio qualification status](docs/audio/MILESTONE-3-STATUS-2026-07-31.md)
- [Library, scanning, and playlists](docs/LIBRARY.md)
- [Lyrics, timing, local files, and optional lookup](docs/LYRICS.md)
- [Portable mode](docs/PORTABLE-MODE.md)
- [Interface and navigation](docs/UI.md)
- [Settings and customization](docs/SETTINGS.md)
- [Milestone 6 settings status](docs/MILESTONE-6-STATUS-2026-08-24.md)
- [Third-party notices](docs/THIRD-PARTY-NOTICES.md)
- [Windows shortcuts and media controls](docs/WINDOWS-INTEGRATION.md)
- [Project architecture](docs/ARCHITECTURE.md)
- [Consumer-readiness roadmap](docs/ROADMAP.md)
- [Performance fixtures and benchmark setup](docs/PERFORMANCE.md)
- [Developer diagnostics and support bundles](docs/DIAGNOSTICS.md)
- [Reliability, recovery, and data safety](docs/RELIABILITY.md)
- [App-data and uninstall behavior](docs/APP-DATA.md)

## Project status

The settings and customization milestone is complete. The current focus is interface consistency, accessibility, Windows integration, and the remaining hardware-gated audio qualification. Bug reports and focused pull requests are welcome through [GitHub Issues](https://github.com/moonnomi/Dextromethorphan/issues).

<details>
<summary>Third-party software and licenses</summary>

NAudio and NVorbis are MIT licensed; BunLabs.NAudio.Flac is MS-PL; Microsoft.Data.Sqlite is MIT; SQLitePCLRaw and the DST decoder are Apache-2.0; and TagLibSharp and SoundTouch.Net are LGPL-2.1. Required SoundTouch and DST notices are copied into release output under `licenses`. Replace the LGPL/MS-PL components if a permissive-only distribution policy is required.

</details>
