# CUE sheets and single-image albums

Date: 2026-07-31

This report is the evidence for DEC-005.

## Behavior

- UTF-8, BOM-based Unicode, and the current Windows ANSI code page are
  accepted.
- Global and per-track `TITLE` and `PERFORMER`, plus `REM GENRE`, `REM DATE`,
  `REM DISCNUMBER`, `FILE`, `TRACK AUDIO`, and `INDEX 01` are parsed.
- A single image and multi-file sheets are supported. The next `INDEX 01` in
  the same image defines the current track's end; the decoded media duration
  defines the final end.
- The referenced image is not also indexed as a duplicate full-length track.
- Each CUE entry receives a stable virtual library identity while retaining
  its real media path, sheet path, and millisecond start/end boundaries.
- Ratings, love state, play count, playlists, and bookmarks therefore remain
  per CUE entry. Removed entries are marked missing instead of being silently
  deleted, preserving recoverable user state.
- The decoder wraps the real media stream in a bounded `SegmentWaveStream`.
  Seeking is relative to the CUE track, reads stop at the boundary, and normal
  gapless/queue behavior sees the segment duration rather than the full image.
- File watchers reconcile changed sheets and their referenced media, then ask
  the UI for one coalesced refresh.

## Data safety

Scanning and playback open referenced media read-only. CUE metadata is stored
in the local SQLite index; neither the CUE sheet nor its image is rewritten.
Schema migration 5 adds nullable media/sheet paths and segment offsets. The
existing migration guard creates a database backup before upgrading and
restores it if migration fails.

The automated single-image test copies the generated 997 Hz WAV fixture into
an isolated temporary library, hashes it, scans two CUE entries, verifies
metadata and boundaries after database persistence, compares decoded segment
samples with the source image, seeks within the segment, drains it to stable
EOF, and verifies the source hash is unchanged. A second test verifies that
reconciliation marks a removed CUE entry missing without deleting its row.

## Qualification result

```powershell
dotnet test Dextromethorphan.slnx --no-restore
```

Result on 2026-07-31: 195/195 tests passed, including 2/2 dedicated CUE tests.

## Boundary

Chapters embedded in containers are tracked separately by DEC-006. CUE data
tracks are ignored; only `TRACK ... AUDIO` entries with `INDEX 01` are exposed
as playable tracks.
