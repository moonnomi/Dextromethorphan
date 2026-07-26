# Consumer-readiness roadmap

This is the master backlog for turning Dextromethorphan from a capable personal project into a dependable consumer-grade Windows music player. It is a living document: when new defects are found, add them under the relevant area instead of creating a second roadmap.

## How to use this list

- **P0** blocks reliable daily use or makes later work difficult.
- **P1** is required for the first consumer-ready release.
- **P2** is important polish or an advanced feature that can follow the first stable release.
- **P3** is optional, experimental, or provider-dependent.
- Each item has a stable ID so work can be discussed and committed one item at a time.
- A checked item means the behavior exists and has been verified, not merely that supporting backend code exists.

## Current foundation

- [x] BASE-001 Native WPF/.NET Windows shell with custom chrome and responsive library tabs.
- [x] BASE-002 SQLite library, full-text search, transactional scanning, folder watching, and artwork disk cache.
- [x] BASE-003 Albums, artists, genres, songs, folders, playlists, favorites, collection tabs, and Now Playing views.
- [x] BASE-004 Temporary queue with play-next, add, replace, reordering, shuffle, repeat, undo/redo, and session restore.
- [x] BASE-005 Event-driven WASAPI shared/exclusive engine with direct and DSP paths.
- [x] BASE-006 Gapless playback, crossfade, ReplayGain, fades, software volume, speed/pitch, and DoP foundations.
- [x] BASE-007 Synced LRC/enhanced-LRC parsing, active-line tracking, auto-scroll, and click-to-seek.
- [x] BASE-008 Rebindable shortcut backend, global hotkeys, media keys, and Windows media transport controls.
- [x] BASE-009 JSON settings with migrations and atomic persistence.
- [x] BASE-010 Themed track and queue context menus.
- [x] BASE-011 Release script, x64/ARM64 targets, Inno Setup project, and self-contained publish support.
- [x] BASE-012 Automated unit/integration foundation with 38 passing tests.

---

## Milestone 1 — responsiveness and performance

This is the next milestone. Do not mask stalls with longer animations; remove the work causing the stalls.

### Measurement and performance budgets

- [x] **P0 PERF-001** Add a repeatable 10k-track and 50k-track synthetic library benchmark fixture with legal generated metadata/art. See [Performance fixtures](PERFORMANCE.md).
- [x] **P0 PERF-002** Record baseline cold start, warm start, tab-switch latency, first-art latency, scroll frame time, scan throughput, memory, and idle/playback CPU. See [the 2026-07-25 baseline](performance/BASELINE-2026-07-25.md).
- [x] **P0 PERF-003** Add local timing instrumentation around repository queries, group construction, tab application, artwork lookup/decode, and first render. See [Developer diagnostics](DIAGNOSTICS.md).
- [x] **P0 PERF-004** Add an opt-in developer performance overlay showing UI-thread stalls, image queue depth, cache hit rate, working set, GC counts, and current frame time. See [Developer diagnostics](DIAGNOSTICS.md#live-performance-overlay).
- [x] **P0 PERF-005** Define release gates: cold start under 3 seconds at 10k tracks, cached tab switch under 100 ms, no routine UI stall over 50 ms, smooth 60 Hz scrolling on the target machine, idle CPU under 5%, and memory under 300 MB at 50k tracks without full-size art preloading. See [Performance release gates](performance/RELEASE-GATES.md).
- [ ] **P1 PERF-006** Add automated performance regression runs that compare results against a stored baseline with an explicit tolerance.

### Artwork pipeline

- [x] **P0 ART-001** Replace synchronous file existence checks, file opens, and bitmap decoding in `ArtworkThumbnailConverter` with an asynchronous artwork service. See [the artwork and virtualization result](performance/ARTWORK-VIRTUALIZATION-2026-07-25.md).
- [x] **P0 ART-002** Decode images off the UI thread, freeze the resulting `BitmapSource`, and marshal only the final property update to the dispatcher.
- [x] **P0 ART-003** Deduplicate concurrent requests for the same artwork path and requested size.
- [x] **P0 ART-004** Use a size-bounded strong LRU memory cache instead of weak references that allow decode/GC/decode churn.
- [ ] **P0 ART-005** Prioritize visible cards, the playing track, and queue entries; defer off-screen work.
- [x] **P0 ART-006** Cancel stale artwork requests immediately when the query, tab, collection, or library generation changes.
- [ ] **P0 ART-007** Resolve artwork only for the active view instead of starting work for albums, artists, genres, folders, and playlists together.
- [ ] **P0 ART-008** Coalesce artwork property changes into small dispatcher batches rather than one UI dispatch per card.
- [ ] **P1 ART-009** Generate persistent 64, 256, 640, and Now Playing thumbnail variants so normal navigation never decodes original embedded artwork.
- [ ] **P1 ART-010** Store the detected image format/extension and reject corrupt or decompression-bomb artwork safely.
- [ ] **P1 ART-011** Add placeholders, fade-in, retry, and permanent-failure states without layout shifts.
- [ ] **P1 ART-012** Prune memory and disk caches predictably; expose current size, clear-cache, and rebuild-cache actions.
- [ ] **P1 ART-013** Invalidate thumbnails when media modification time, embedded art, or an external cover changes.
- [ ] **P2 ART-014** Support preferred external covers such as `cover`, `folder`, and `front` with deterministic precedence.

### View virtualization and state changes

- [x] **P0 VIEW-001** Replace the gallery `WrapPanel` with a virtualizing wrap panel; incremental loading alone is not virtualization. See [the artwork and virtualization result](performance/ARTWORK-VIRTUALIZATION-2026-07-25.md).
- [ ] **P0 VIEW-002** Preserve the scroll position and selected item independently for every primary and collection tab.
- [ ] **P0 VIEW-003** Stop rebuilding or replacing view collections when switching to already-indexed tabs.
- [ ] **P0 VIEW-004** Replace `ObservableCollection.Clear` plus per-track `Add` loops with range replacement or an immutable/paged view source.
- [ ] **P0 VIEW-005** Avoid retaining a separate full track-array projection for every album, artist, genre, and folder; use lightweight group keys and lazy track queries/indexes.
- [ ] **P0 VIEW-006** Remove the playlist N+1 query pattern by loading playlist summaries and tracks in a bounded or joined operation.
- [ ] **P0 VIEW-007** Ensure hidden views do not keep expensive effects, bindings, and image sources alive.
- [ ] **P1 VIEW-008** Page or window the Songs view instead of loading every database row into presentation state.
- [ ] **P1 VIEW-009** Cache stable grouping/sorting indexes and update only affected groups after file-watcher changes.
- [ ] **P1 VIEW-010** Move the largest reusable views into separate controls so WPF does not parse and retain one monolithic visual tree.

### Scrolling and rendering

- [ ] **P0 SCROLL-001** Profile the custom per-frame smooth-scroll loop on mouse wheels and precision touchpads.
- [ ] **P0 SCROLL-002** Support high-resolution touchpad deltas without swallowing horizontal scrolling or nested-scroll handoff.
- [ ] **P0 SCROLL-003** Stop the render callback immediately when a viewer unloads, becomes hidden, or reaches its target.
- [ ] **P0 SCROLL-004** Avoid layout-triggering work, image decode, and collection mutation during active scrolling.
- [ ] **P0 SCROLL-005** Reduce or cache software-rendered blur and drop-shadow effects, especially on Now Playing.
- [ ] **P1 SCROLL-006** Respect Windows reduced-motion and animation settings in addition to the app toggle.
- [ ] **P1 SCROLL-007** Add keyboard, page, Home/End, and scrollbar scrolling tests for every list.

### Startup, scanning, and memory

- [ ] **P0 SYS-001** Split startup into shell-ready and background-library phases so the window is interactive before nonessential grouping/artwork work completes.
- [ ] **P0 SYS-002** Coalesce scan progress updates so thousands of files cannot flood the dispatcher.
- [ ] **P0 SYS-003** Add scan cancellation, pause/resume, and resumable checkpoints for large or network libraries.
- [ ] **P0 SYS-004** Bound scanner memory and metadata concurrency independently for SSD, HDD, removable, and SMB sources.
- [ ] **P0 SYS-005** Profile and eliminate event-handler, bitmap, cancellation-token, and view-model retention leaks.
- [ ] **P1 SYS-006** Add an idle cleanup policy for thumbnails, navigation history, stale group state, and completed background jobs.
- [ ] **P1 SYS-007** Hardware-test scan throughput and responsiveness at 10k, 50k, and 100k tracks.

### Milestone 1 exit criteria

- [ ] **P0 PERF-GATE-001** Switching among cached Albums, Artists, Genres, Songs, Folders, and Playlists is visually immediate and does not start irrelevant artwork work.
- [ ] **P0 PERF-GATE-002** Rapidly scrolling an uncached album grid does not produce repeated long UI-thread stalls.
- [ ] **P0 PERF-GATE-003** Returning through Mouse4/Mouse5 history restores state without rebuilding the view.
- [ ] **P0 PERF-GATE-004** Scanning and playback can run together without audio underruns or visible navigation degradation.
- [ ] **P0 PERF-GATE-005** Performance budgets are captured in a reproducible report before and after optimization.

---

## Milestone 2 — reliability, recovery, and data safety

### Application lifecycle

- [ ] **P0 REL-001** Add single-instance behavior and forward files/folders from later launches to the existing window.
- [ ] **P0 REL-002** Replace raw exception message boxes with a themed error surface that offers Copy details, Open log folder, and Continue/Restart where safe.
- [ ] **P0 REL-003** Add structured rotating local logs for startup, scanning, database migrations, audio state, device changes, and handled failures.
- [ ] **P0 REL-004** Capture dispatcher, task, and AppDomain unhandled exceptions without hiding corrupted state.
- [ ] **P0 REL-005** Make shutdown cancellation-aware and verify queue, position, bookmarks, settings, and database work are flushed.
- [ ] **P0 REL-006** Recover cleanly from power loss or process termination during settings save, artwork write, scan batch, and playlist edit.
- [ ] **P1 REL-007** Add a safe mode that disables custom themes/effects and skips session resume after repeated startup failure.
- [ ] **P1 REL-008** Add a diagnostics bundle export containing redacted settings, logs, device capabilities, version, and database schema—not the music library itself by default.

### Database and settings

- [ ] **P0 DATA-001** Introduce explicit numbered SQLite migrations with upgrade, rollback/restore, and fixture coverage.
- [ ] **P0 DATA-002** Back up the database before destructive migrations and retain a small rotating set.
- [ ] **P0 DATA-003** Detect corruption, run integrity checks when appropriate, and offer rebuild-from-files without losing playlists/ratings where recoverable.
- [ ] **P0 DATA-004** Validate and normalize every settings field at load, including colors, fonts, paths, buffers, rates, and device IDs.
- [ ] **P0 DATA-005** Add settings export/import and a Reset section with scoped reset options.
- [ ] **P1 DATA-006** Add explicit backup/restore for playlists, ratings, love state, play history, bookmarks, and settings.
- [ ] **P1 DATA-007** Document app-data layout and define uninstall behavior that never silently deletes the user database.

### Filesystem and network-source failure handling

- [ ] **P0 FILE-001** Treat disconnected SMB, removable, and mounted sources as offline instead of deleting their tracks.
- [ ] **P0 FILE-002** Show per-source status, last successful scan, errors, offline state, watcher state, and track count.
- [ ] **P0 FILE-003** Handle rename/move detection without losing ratings, play history, bookmarks, or playlist references.
- [ ] **P0 FILE-004** Skip corrupt/unsupported tracks during queue playback, explain the failure, and continue according to queue/repeat rules.
- [ ] **P0 FILE-005** Handle long paths, Unicode normalization, inaccessible directories, symlink/junction loops, and case-insensitive duplicates.
- [ ] **P1 FILE-006** Add duplicate detection by canonical path and optional audio/content fingerprint.
- [ ] **P1 FILE-007** Add a Missing files view with locate, relink, remove, and rescan actions.

---

## Milestone 3 — audio engine completion and qualification

### Output devices and modes

- [ ] **P0 AUD-001** Build the full per-output profile editor: device, shared/exclusive, buffer, sample rate policy, bit depth, channel policy, fallback, hardware/software volume, and DSD mode.
- [ ] **P0 AUD-002** Enumerate and display supported exclusive formats before saving a profile.
- [ ] **P0 AUD-003** Handle default-device changes, device removal, sleep/resume, driver reset, and endpoint invalidation without freezing or changing Windows volume.
- [ ] **P0 AUD-004** Make exclusive failure/fallback visible and actionable; never silently claim bit-perfect playback.
- [ ] **P0 AUD-005** Add configurable retry/backoff and automatic continuation after recoverable endpoint errors.
- [ ] **P0 AUD-006** Qualify 44.1, 48, 88.2, 96, 176.4, and 192 kHz; 16/24/32-bit; mono/stereo; shared/exclusive; and buffer boundaries.
- [ ] **P1 AUD-007** Define stereo-only behavior for multichannel files and endpoints: reject, map, or downmix with explicit policy.
- [ ] **P1 AUD-008** Add Bluetooth/HDMI-friendly shared-mode profiles and sensible device-specific defaults.
- [ ] **P1 AUD-009** Export an audio diagnostics report including requested/effective format, decoder, pipeline, fallback reason, underruns, callback timing, and device ID.

### Decoder and format coverage

- [ ] **P0 DEC-001** Maintain a generated/legal format corpus covering FLAC, MP3, WAV, AIFF, Ogg Vorbis, Opus, AAC/M4A, ALAC, WMA, DSF, and DFF.
- [ ] **P0 DEC-002** Bundle or validate dependable decoder coverage on a clean supported Windows installation; do not rely on an assumed Media Foundation transform.
- [ ] **P0 DEC-003** Test malformed headers, truncated files, unusual metadata blocks, large tags, embedded covers, VBR duration, and Unicode paths.
- [ ] **P0 DEC-004** Verify seeking accuracy and end-of-stream behavior for every supported codec.
- [ ] **P1 DEC-005** Add CUE sheet and single-image album support with per-track boundaries and metadata.
- [ ] **P1 DEC-006** Add chapters for formats that support them.
- [ ] **P2 DEC-007** Add DST-compressed DFF support.
- [ ] **P3 DEC-008** Evaluate ASIO/native DSD only after WASAPI/DoP is hardware-qualified.

### Signal processing and transitions

- [ ] **P0 DSP-001** Add offline waveform/audio fixtures that verify no silence, duplication, or dropped frames at gapless boundaries.
- [ ] **P0 DSP-002** Verify crossfade timing and equal-power behavior across differing rates/channels and very short tracks.
- [ ] **P0 DSP-003** Add ReplayGain scanner/analysis for files missing tags, using an EBU R128-compatible implementation.
- [ ] **P0 DSP-004** Add album/track gain, preamp, clipping prevention, and processing-state controls to Settings.
- [ ] **P1 DSP-005** Replace the current interpolator/FFT pitch path with a reviewed high-quality tempo/pitch engine after listening, latency, and license evaluation.
- [ ] **P1 DSP-006** Add an oversampled true-peak limiter or clearly label the current sample-peak guard.
- [ ] **P1 DSP-007** Measure processing latency and keep timeline, lyrics, and SMTC position aligned.
- [ ] **P1 DSP-008** Add optional loudness/peak analysis jobs that are cancellable and do not degrade playback.

### Hardware qualification

- [ ] **P0 HW-001** Test at least one normal onboard device before every release.
- [ ] **P1 HW-002** Build a DAC test matrix for exclusive negotiation, sample-rate switching, buffers, hardware volume, device loss, and long playback.
- [ ] **P1 HW-003** Verify DoP marker order, channel interleave, seeking, DSD64/128 negotiation, and DAC indication on physical hardware.
- [ ] **P1 HW-004** Run 8-hour playback/transition soak tests and record underruns and memory growth.

---

## Milestone 4 — library and metadata product features

### Sources and scanning UI

- [ ] **P1 LIB-001** Add a source manager with Add, Remove, Enable, Rescan, exclusions, watcher toggle, and per-source status.
- [ ] **P1 LIB-002** Add scan progress with discovered/processed/added/updated/failed counts, current source, cancel, and failure details.
- [ ] **P1 LIB-003** Support drag-and-drop of folders and supported files into the app.
- [ ] **P1 LIB-004** Add scheduled/background scan options without running surprise work on battery or metered networks.
- [ ] **P1 LIB-005** Build a real hierarchical folder tree, not only a flat list of directories containing tracks.
- [ ] **P2 LIB-006** Add portable mode with app data beside the executable.

### Metadata correctness and editing

- [ ] **P1 META-001** Verify ID3v2.2/2.3/2.4, Vorbis, MP4, APE, ASF/WMA, and RIFF/AIFF tag mappings.
- [ ] **P1 META-002** Define multi-value artist/genre parsing without splitting legitimate names incorrectly; make separators configurable.
- [ ] **P1 META-003** Correctly group album artist, featured artists, compilations, disc sets, release types, sort tags, and classical metadata.
- [ ] **P1 META-004** Add single- and multi-track tag editing with preview, validation, undo, and atomic file writes.
- [ ] **P1 META-005** Let users choose database-only edits or write-back to files per field/action.
- [ ] **P1 META-006** Add embedded/external artwork view, replace, remove, crop, and preferred-cover selection.
- [ ] **P1 META-007** Preserve unknown/custom tags during edits.
- [ ] **P2 META-008** Add opt-in MusicBrainz/Discogs metadata matching with confirmation before writes.
- [ ] **P2 META-009** Add artist images, biographies, and library statistics with offline caching and source attribution.

### Browsing, sorting, and filtering

- [ ] **P1 BROWSE-001** Add per-view sort choices and persist them independently.
- [ ] **P1 BROWSE-002** Add compact/list/grid density controls and remember cover size per view.
- [ ] **P1 BROWSE-003** Add persistent quick filters such as lossless, codec, year, rating, loved, compilation, and source.
- [ ] **P1 BROWSE-004** Add configurable list columns with show/hide/reorder/width persistence.
- [ ] **P1 BROWSE-005** Add multi-select with Ctrl/Shift, Select all, and keyboard/context actions.
- [ ] **P1 BROWSE-006** Add drag-and-drop from tracks/collections to queue and playlists with clear insertion feedback.
- [ ] **P1 BROWSE-007** Add album disc grouping, headers, totals, and album-level ReplayGain/metadata indicators.
- [ ] **P1 BROWSE-008** Add richer artist pages with albums, singles/EPs, compilations, appearances, top tracks, and stats.
- [ ] **P1 BROWSE-009** Add recently added, recently played, most played, never played, and history views.
- [ ] **P2 BROWSE-010** Add user-configurable home/dashboard modules.

### Search

- [ ] **P1 SEARCH-001** Add scoped filters for title, artist, album, genre, filename, comment, playlist, source, codec, and year.
- [ ] **P1 SEARCH-002** Add search suggestions, recent searches, clear-history, and keyboard navigation.
- [ ] **P1 SEARCH-003** Highlight matches and show grouped result counts without rebuilding unrelated views.
- [ ] **P1 SEARCH-004** Add exact phrase, exclusion, and structured filter syntax with safe parsing.
- [ ] **P1 SEARCH-005** Test diacritics, CJK, RTL text, punctuation, multi-artist values, and very large result sets.

### Playlists

- [ ] **P1 PL-001** Add create, rename, delete, duplicate, and description/cover editing.
- [ ] **P1 PL-002** Add ordered multi-select editing and drag/drop between playlists and queue.
- [ ] **P1 PL-003** Add the smart-playlist rule builder for nested AND/OR groups, validation, preview count, sort, and limit.
- [ ] **P1 PL-004** Expose M3U8, PLS, and XSPF import/export with conflict and missing-file reporting.
- [ ] **P1 PL-005** Add Save queue as playlist and Add collection/selection to playlist.
- [ ] **P1 PL-006** Add undo/redo for playlist edits and clear user feedback after each operation.
- [ ] **P2 PL-007** Add automatic playlist backups and portable relative-path export.

---

## Milestone 5 — playback, queue, lyrics, and daily-use controls

### Playback and queue

- [ ] **P1 PLAY-001** Expose bookmark create, rename, seek, remove, and automatic resume controls.
- [ ] **P1 PLAY-002** Expose sleep timer presets, custom time, end of track, end of queue, fade-out, and visible remaining time.
- [ ] **P1 PLAY-003** Expose speed, pitch, preserve-pitch, reset, and per-track override controls.
- [ ] **P1 PLAY-004** Add Stop after current and Stop after queue controls with persistent indicators.
- [ ] **P1 PLAY-005** Add clickable 0–5 star rating, Love, and clear-rating actions in track rows and Now Playing.
- [ ] **P1 PLAY-006** Add seek hover tooltip, chapter markers, bookmark markers, and optional waveform.
- [ ] **P1 PLAY-007** Add queue insertion markers, multi-select, remove selected, move to top/bottom, and keyboard reordering.
- [ ] **P1 PLAY-008** Add queue history/previously played and a clear distinction between current, next, and later items.
- [ ] **P1 PLAY-009** Confirm shuffle order is stable, avoids immediate repeats, survives edits, and restores across sessions.
- [ ] **P1 PLAY-010** Add user-visible undo/redo notifications and redo access for queue changes.
- [ ] **P1 PLAY-011** Add playback-error rows/toasts that explain unsupported/corrupt/offline files and provide Locate/Remove/Skip.
- [ ] **P2 PLAY-012** Add an optional compact mini-player and fullscreen Now Playing mode.

### Lyrics

- [ ] **P1 LYR-001** Finish line highlighting, word highlighting, previous/current/next emphasis, and smooth centered scrolling across long files.
- [ ] **P1 LYR-002** Add a manual synchronization offset control with per-track persistence.
- [ ] **P1 LYR-003** Add static/synced mode selection, font size, alignment, line spacing, blur strength, and reduced-motion behavior.
- [ ] **P1 LYR-004** Support multiple timed lines at the same timestamp, instrumental gaps, translations, and romanized lines.
- [ ] **P1 LYR-005** Add local lyric discovery priority, reload, choose alternate file, edit, save, and remove.
- [ ] **P1 LYR-006** Handle malformed encodings, BOMs, very long lines, and mixed timestamp formats gracefully.
- [ ] **P2 LYR-007** Add an opt-in lyric provider with source attribution, rate limiting, caching, and manual confirmation.
- [ ] **P2 LYR-008** Add karaoke-style per-word animation that remains synchronized under seek and speed changes.

### Optional visual playback features

- [ ] **P2 VIS-001** Add a low-overhead FFT spectrum visualizer that can be fully disabled.
- [ ] **P2 VIS-002** Add a cached waveform seek bar generated in the background.
- [ ] **P2 VIS-003** Verify visualizers do not alter the direct audio path or cause callback underruns.

---

## Milestone 6 — complete settings and customization

- [ ] **P1 SET-001** Expose every existing `AppSettings` value in the UI; no important option should require JSON editing.
- [ ] **P1 SET-002** Add live Dark, Light, and AMOLED themes with a working accent picker and contrast validation.
- [ ] **P1 SET-003** Add font family/size, density, background opacity, animation, fullscreen, and queue-panel controls.
- [ ] **P1 SET-004** Add complete playback transition, fade, resume, stop, ReplayGain, speed, pitch, and clipping controls.
- [ ] **P1 SET-005** Add complete library source, exclusion, watcher, scan, cache, and tag-writing controls.
- [ ] **P1 SET-006** Add a shortcut editor with capture, validation, conflicts, global/in-app scope, enable/disable, reset, and import/export.
- [ ] **P1 SET-007** Add per-view sorting, filters, cover size, columns, density, and reset-to-default.
- [ ] **P1 SET-008** Add Apply/Cancel semantics where changes are risky; keep safe visual changes live.
- [ ] **P1 SET-009** Add search within Settings and link diagnostics/help from relevant controls.
- [ ] **P1 SET-010** Add About with version, runtime, build commit, app-data paths, licenses, privacy, and update status.

---

## Milestone 7 — UI polish, accessibility, and interaction consistency

### Visual and interaction system

- [ ] **P1 UX-001** Create shared design tokens for spacing, radii, type scale, elevations, states, and motion durations.
- [ ] **P1 UX-002** Break the main shell and settings into reusable themed controls; remove one-off inline styles.
- [ ] **P1 UX-003** Theme every popup, context menu, tooltip, dialog, combo box, checkbox, radio button, progress indicator, and scrollbar.
- [ ] **P1 UX-004** Add consistent loading, empty, offline, error, disabled, and success states.
- [ ] **P1 UX-005** Add nonintrusive themed toasts for queue, playlist, scan, settings, and playback actions.
- [ ] **P1 UX-006** Add rich metadata tooltips with full title/path/codec/rate/bit depth/bitrate where useful.
- [ ] **P1 UX-007** Validate the layout at 800×600, common laptop sizes, ultrawide, and queue-visible/hidden combinations.
- [ ] **P1 UX-008** Validate PerMonitorV2 DPI behavior at 100%, 125%, 150%, 200%, mixed-DPI monitors, and monitor changes.
- [ ] **P1 UX-009** Restore window placement safely when monitors are removed or resolution changes.
- [ ] **P1 UX-010** Integrate Windows 11 snap-layout behavior or document the custom-titlebar limitation and provide an equivalent.
- [ ] **P2 UX-011** Add configurable dock/collapse behavior for queue, lyrics, and metadata panels.
- [ ] **P3 UX-012** Add detachable/floating panels only after state, focus, and multi-window ownership are reliable.

### Accessibility and input

- [ ] **P1 A11Y-001** Make every control reachable and operable by keyboard with logical focus order.
- [ ] **P1 A11Y-002** Add visible focus states and meaningful AutomationProperties names/help text.
- [ ] **P1 A11Y-003** Test Narrator for navigation, track metadata, transport state, sliders, queue state, and dialogs.
- [ ] **P1 A11Y-004** Meet WCAG AA contrast for text and state indicators; never encode state by color alone.
- [ ] **P1 A11Y-005** Support Windows High Contrast and reduced-motion settings.
- [ ] **P1 A11Y-006** Support keyboard context-menu invocation, access keys, and full list multi-selection.
- [ ] **P1 A11Y-007** Make seek/volume sliders announce values and support fine/coarse keyboard increments.
- [ ] **P1 A11Y-008** Audit touch, pen, mouse, precision touchpad, Mouse4/Mouse5, media keys, and remote-control input.
- [ ] **P2 A11Y-009** Prepare all user-facing strings for localization and verify RTL layout.

---

## Milestone 8 — Windows integration

- [ ] **P1 WIN-001** Publish album artwork, track number, and complete metadata to Windows media controls.
- [ ] **P1 WIN-002** Verify play/pause/next/previous/stop/seek and media keys across lock screen, flyout, sleep/resume, and multiple media apps.
- [ ] **P1 WIN-003** Add file associations and Open with support for supported audio and playlist formats.
- [ ] **P1 WIN-004** Add command-line/open-file activation and enqueue/replace behavior.
- [ ] **P1 WIN-005** Add jump-list tasks for Now Playing, Play/Pause, and common library destinations if they remain useful.
- [ ] **P1 WIN-006** Add optional startup behavior without requiring administrator privileges.
- [ ] **P1 WIN-007** Ensure global hotkeys unregister/re-register correctly after sleep, settings changes, and another app conflict.
- [ ] **P2 WIN-008** Add optional taskbar thumbnail controls and progress.
- [ ] **P2 WIN-009** Add optional playback/scan notifications with quiet-hours compliance.

---

## Milestone 9 — engineering quality and maintainability

### Privacy and security

- [ ] **P0 SEC-001** Add a test-enforced offline default: no metadata, lyrics, update, scrobble, or other network request before explicit opt-in.
- [ ] **P0 SEC-002** Treat media files, tags, artwork, lyrics, playlists, and imported XML as untrusted input with size, depth, time, and memory limits.
- [ ] **P0 SEC-003** Audit path canonicalization, relative playlist paths, UNC sources, junctions, and archive/import operations for traversal and unintended file access.
- [ ] **P0 SEC-004** Redact usernames, library paths, device identifiers, and credentials from logs/diagnostic exports unless the user explicitly includes them.
- [ ] **P1 SEC-005** Add automated dependency vulnerability review and a documented dependency-update cadence.
- [ ] **P1 SEC-006** Keep provider credentials in Windows Credential Manager and wipe them when a provider is disconnected.
- [ ] **P1 SEC-007** Require signed update metadata and verify package hash/signature before any update is offered or applied.
- [ ] **P1 SEC-008** Publish a plain-language local-data/privacy inventory with clear delete-history, clear-cache, and reset-app-data controls.
- [ ] **P1 SEC-009** Complete a threat model before enabling scripts, extensions, local-network sync, or remote control.

### Architecture

- [ ] **P0 ENG-001** Split the large `MainViewModel` into library, navigation, playback, queue, lyrics, settings, and diagnostics components with explicit ownership.
- [ ] **P0 ENG-002** Split `MainWindow.xaml` and code-behind into focused views/controls without changing behavior.
- [ ] **P0 ENG-003** Introduce a dedicated asynchronous artwork loader/cache abstraction for testability.
- [ ] **P0 ENG-004** Remove fire-and-forget tasks without explicit error handling, lifetime, and cancellation.
- [ ] **P0 ENG-005** Audit UI-bound collection access and dispatcher ownership.
- [ ] **P1 ENG-006** Add nullable/static analyzers, warnings-as-errors in CI, formatting, and dependency audit.
- [ ] **P1 ENG-007** Add structured logging abstractions instead of direct file appends and status-text exceptions.
- [ ] **P1 ENG-008** Add design-time/sample data for isolated UI development.
- [ ] **P1 ENG-009** Document threading, cancellation, ownership, audio callback, and database rules.
- [ ] **P1 ENG-010** Keep public service contracts small and replace service-locator access with constructor dependencies.

### Automated testing

- [ ] **P0 TEST-001** Add tests for tab navigation state, collection selection, session restore, previous-track threshold, seek state, and error skip behavior.
- [ ] **P0 TEST-002** Add artwork loader/cache tests for deduplication, cancellation, eviction, corruption, invalidation, and concurrency.
- [ ] **P0 TEST-003** Add scanner tests for cancellation, offline roots, watcher overflow, rename/move, exclusions, and partial failure.
- [ ] **P0 TEST-004** Add database migration, backup, integrity, and corruption-recovery tests.
- [ ] **P0 TEST-005** Add audio integration tests for every decoder using legal generated fixtures.
- [ ] **P1 TEST-006** Add WPF UI automation smoke tests for launch, add folder, navigate, search, play, seek, queue, settings, context menus, and shutdown.
- [ ] **P1 TEST-007** Add accessibility automation checks and keyboard-only workflows.
- [ ] **P1 TEST-008** Add randomized queue/playlist state-machine tests.
- [ ] **P1 TEST-009** Add file/tag/parser fuzz and malformed-input tests with strict time/memory limits.
- [ ] **P1 TEST-010** Add long-running playback, scan/playback concurrency, suspend/resume, and device-loss tests.
- [ ] **P1 TEST-011** Add clean-machine installer/uninstaller and upgrade tests.

### CI and release discipline

- [ ] **P1 CI-001** Add GitHub Actions for restore, build, tests, analyzers, dependency review, and x64/ARM64 publish.
- [ ] **P1 CI-002** Add deterministic versioning, changelog, tagged releases, checksums, and release notes.
- [ ] **P1 CI-003** Generate third-party notices, license inventory, and an SBOM.
- [ ] **P1 CI-004** Add code signing for executables/installers to reduce SmartScreen friction.
- [ ] **P1 CI-005** Add signed update manifests and a secure opt-in update checker, or document manual update policy.
- [ ] **P1 CI-006** Keep release artifacts outside source `bin`; clean stale outputs before packaging.

---

## Milestone 10 — packaging and consumer release

- [ ] **P1 DIST-001** Choose and document a versioning policy and first stable-release criteria.
- [ ] **P1 DIST-002** Decide traditional installer, MSIX, portable ZIP, or a supported combination.
- [ ] **P1 DIST-003** Produce x64 and ARM64 packages and test each on clean supported Windows 10/11 systems.
- [ ] **P1 DIST-004** Add installer upgrade, repair, downgrade-block, uninstall, Start menu, desktop shortcut, and file-association behavior.
- [ ] **P1 DIST-005** Optimize publish size and startup with measured ReadyToRun/single-file/trimming decisions; do not trim WPF blindly.
- [ ] **P1 DIST-006** Add a first-run experience for choosing folders, audio output, privacy/network defaults, and importing playlists.
- [ ] **P1 DIST-007** Add an in-app migration/what’s-new experience for meaningful upgrades.
- [ ] **P1 DIST-008** Add a project license, application license notice, privacy statement, and third-party acknowledgements.
- [ ] **P1 DIST-009** Add user-facing supported-format/device limitations and troubleshooting documentation.
- [ ] **P1 DIST-010** Define support boundaries for Windows versions, architectures, codecs, network sources, and DAC modes.

---

## Milestone 11 — privacy-respecting optional services

All network features remain disabled until explicitly enabled by the user.

- [ ] **P2 NET-001** Add a central network/privacy page listing every provider, permission, cache, credential, and last request.
- [ ] **P2 NET-002** Add Last.fm scrobbling with secure credential storage, offline queueing, now-playing updates, retry, and user controls.
- [ ] **P2 NET-003** Add MusicBrainz metadata/artwork lookup with preview, attribution, rate limits, and confirmation.
- [ ] **P2 NET-004** Add one legally usable lyrics provider with attribution, caching, manual matching, and opt-in behavior.
- [ ] **P2 NET-005** Store API credentials in Windows Credential Manager, never plain settings JSON.
- [ ] **P2 NET-006** Add cache clear/export and provider-disable behavior that works fully offline afterward.

---

## Milestone 12 — advanced and stretch features

- [ ] **P3 EXT-001** Define a narrow, versioned extension API before embedding a scripting runtime.
- [ ] **P3 EXT-002** Add opt-in Lua or Python actions with explicit permissions, timeouts, cancellation, and filesystem/network sandboxing.
- [ ] **P3 EXT-003** Add safe examples such as copy current path, custom tag action, and lyric lookup.
- [ ] **P3 SYNC-001** Add playlist/file export to MTP devices with dry-run, capacity checks, filename rules, and conflict handling.
- [ ] **P3 SYNC-002** Add local-network device sync only with authentication, encryption, explicit pairing, and resumable transfer.
- [ ] **P3 DASH-001** Add user-defined home dashboards and movable modules after all core views are stable.

---

## Consumer-ready release gates

A build is not called consumer-ready until all of the following are true:

- [ ] **GATE-001** All P0 items are complete and no known data-loss, Windows-volume, playback-blocking, or routine-crash defect remains.
- [ ] **GATE-002** Required P1 items for the chosen release scope are complete; deferred items are explicitly documented.
- [ ] **GATE-003** Performance budgets pass on the reference 10k/50k libraries.
- [ ] **GATE-004** Playback and scanning soak tests pass without unbounded memory growth or audio underruns.
- [ ] **GATE-005** Core audio paths pass the clean-machine format matrix; exclusive/DoP claims pass the available hardware matrix.
- [ ] **GATE-006** Database upgrade, backup/restore, offline-source, crash-recovery, installer-upgrade, and uninstall flows pass.
- [ ] **GATE-007** Keyboard-only, Narrator, High Contrast, reduced motion, mixed-DPI, and minimum-window workflows pass.
- [ ] **GATE-008** Installer/archive is versioned, signed if distributed, checksummed, license-complete, and reproducible.
- [ ] **GATE-009** User guide, troubleshooting, privacy, supported formats, limitations, and third-party notices match the shipped build.
- [ ] **GATE-010** A final manual daily-use pass covers launch, scan, browse, search, play, seek, lyrics, queue, sleep, resume, device switch, settings, and shutdown.

## Recommended execution order

1. **PERF-001 through PERF-GATE-005** — measure and remove tab/scroll/artwork stalls.
2. **REL/DATA/FILE P0 items** — make failures recoverable and protect the library.
3. **AUD/DEC/DSP P0 items** — complete settings and qualify audio behavior.
4. **Library, playlist, playback, lyrics, and settings P1 items** — expose the backend as a complete product.
5. **UX/A11Y/WIN P1 items** — make every workflow polished and accessible.
6. **Testing, CI, packaging, and release gates** — make releases repeatable.
7. **Optional network, scripting, sync, and dashboard work** — only after the offline core is stable.
