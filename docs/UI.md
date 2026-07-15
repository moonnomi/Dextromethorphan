# Native library shell

The main window uses WPF custom chrome: the library tabs occupy the former title-bar area while native resize behavior and explicit minimize, maximize, and close actions remain available. The default 1360×800 window fits common desktop work areas and can be resized down to 800×600.

Branding is derived from the supplied open-record and waveform mark. Compact surfaces use the symbol alone, while startup uses the full native-text lockup. The startup presentation runs inside the main window, covers real initialization, and adds only a short 420 ms minimum plus a 220 ms reveal.

## Navigation

The primary tabs are real views rather than decorative labels:

- Albums, Artists, and Genres are cover-only gallery pages. Covers remain square at every size.
- Songs presents the complete library; Favorites filters it to loved tracks.
- Folders presents the filesystem-derived library tree as a compact sidebar.
- Playlists loads persisted manual and smart playlists and their ordered tracks.
- The logo and mini-player open Now Playing with artwork and timed lyrics.

Search rebuilds the active groups from matching tracks. Gallery rows are added in small increments near the scroll boundary, so changing tabs does not construct hundreds of image controls at once. Artwork bindings decode fixed-size frozen thumbnails and reuse them while available instead of repeatedly decoding full-resolution embedded art.

Clicking an album, artist, or genre adds a temporary context tab to the top bar. Its focused page contains real artwork when one exists, collection metadata, and the track list. Closing that tab returns to the cover grid. Missing art is left neutral; the UI does not synthesize a gradient cover.

Navigation keeps browser-style history. Mouse4 goes back through primary tabs, sidebar selections, and collection-detail tabs; Mouse5 goes forward. Opening a new destination after going back clears the forward branch.

Selected top-bar tabs animate their accent marker with opacity and transform clocks; view content uses a short fade-and-rise transition. These animations do not trigger layout and create no per-frame application timer. They can be disabled from Settings > Appearance.

Mouse-wheel input is coalesced and eased at render cadence for gallery, track, queue, lyrics, sidebar, and settings scrolling. Track lists retain pixel-based recycling, while galleries still append cards incrementally near the scroll boundary.

## Track and queue presentation

Tracks use a recycling `ListBox`, not a `DataGrid`. Each row keeps a consistent information rhythm—track number, artwork, title/artist, album/year, format, loved state, and duration—without cell borders or table headers. Double-click plays the row; the context menu supports Play, Play next, and Add to queue.

The right queue is temporary and independent from playlists. It can be collapsed from the top bar, highlights the playing entry, exposes undo and clear, and leaves the persistent transport available at every view.

The timeline and volume controls support pointer capture: users can press anywhere on the track and continue dragging beyond the thumb. Timeline position is previewed locally during the drag and committed once on release, preventing playback progress events from fighting the pointer. Volume reaches the audio engine continuously while JSON persistence is debounced.

Settings opens as one reusable non-modal window. Its Audio, Playback, Library, Appearance, and Shortcuts sections use application resources directly, so the window shares the main dark theme and no longer depends on a missing navigation style.
