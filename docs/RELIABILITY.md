# Reliability, recovery, and data safety

Dextromethorphan keeps music files read-only during normal browsing and playback. Library metadata, settings, cache files, and recovery artifacts live under the app-data directory described in [APP-DATA.md](APP-DATA.md).

## What is protected

- A second launch forwards files/folders to the existing process.
- Settings and artwork writes use temporary files plus atomic replacement.
- SQLite mutations are transactional and numbered schema migrations create rotating pre-migration backups.
- Offline SMB, removable, and mounted roots retain their indexed tracks.
- Renames and relinks preserve track identity, ratings, love state, play history, bookmarks, and playlist references.
- Unsupported, inaccessible, or corrupt queue entries are explained, skipped, and playback continues when possible.
- Repeated startup failures trigger safe mode, which disables effects and session resume.
- Database corruption presents recovery choices: restore the newest backup, or rebuild from configured files and reapply recoverable user state.

## User-controlled portability

Settings can be exported/imported or reset by section. A `.dexbackup` archive can explicitly back up and restore settings, playlists, ratings, love state, play history, and bookmarks. It never embeds music files.

Duplicate analysis is read-only: same-size available files are compared by SHA-256 with bounded concurrency. Nothing is deleted automatically.

## Diagnostics privacy

The diagnostics bundle contains version/runtime information, redacted settings, recent structured logs, hashed audio-device identifiers, and database integrity/schema metadata. It excludes the music database and media files by default.
