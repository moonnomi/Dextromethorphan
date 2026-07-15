# Library and playlists

## Scan pipeline

The scanner enumerates supported files without following inaccessible folders, loads the existing path/mtime/size index once, and parses only new or changed files. Metadata readers run concurrently while a bounded single-consumer channel commits groups of 250 tracks in SQLite transactions. This avoids opening a connection and committing a transaction for every file while also applying backpressure on fast disks.

Watch-folder events are debounced. A watcher overflow schedules a full scan of that root. Missing files are removed only beneath roots included in the completed scan; foreign library roots are left untouched.

Embedded artwork is extracted into `%APPDATA%\Dextromethorphan\artwork`. Cache keys include the canonical media path and file modification time, so changed files receive a new version without stale-image collisions. Recently used art is retained up to the configured cache limit and older entries are pruned by last access time. SQLite stores only the resulting cache path.

## Search

`tracks_fts` is an FTS5 external-content index synchronized by insert, update, and delete triggers. It indexes title, artist, album, album artist, genre, path, and comment with Unicode tokenization, diacritic folding, and 2/3/4-character prefix indexes. User input is converted into quoted prefix terms instead of being treated as raw FTS syntax.

The FTS index is created and rebuilt once when an existing database is first upgraded. Subsequent application starts do not rebuild it.

## Smart playlists

Smart playlists persist a typed, nested rule tree as JSON. Groups can match all (`AND`) or any (`OR`) child conditions and nest up to eight levels. SQL generation uses an enum-to-column allowlist and bound values; rule input cannot inject column names or SQL fragments.

Supported fields include metadata, rating/love, play count, last played, date added, duration, codec, bitrate, sample rate, and path. Numbers use invariant formatting, duration values are seconds, dates use ISO-8601, and relative date rules use a number of days. Sort columns are also allowlisted, and results can be capped at 5,000 tracks.

Manual playlists retain their explicit order and reject direct track edits on smart playlists. Queue state remains separate and temporary.

## Playlist files

- M3U8 export writes UTF-8 `#EXTM3U` and `#EXTINF` records.
- PLS export writes version 2 ordered entries.
- XSPF export writes version 1 track locations as file URIs with title, creator, album, and duration.

Imports resolve relative paths against the playlist file. XSPF parsing prohibits DTD processing and external XML resolution. The high-level file service maps imported locations to the current library, creates an ordered manual playlist, and safely skips unresolved entries.
