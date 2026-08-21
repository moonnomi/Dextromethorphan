# Milestone 4 status — library and metadata

Milestone 4 is complete in the current build. The library surface now keeps
the metadata model, browse state, search grammar, and playlist data model in
sync instead of treating each screen as a separate projection.

## What is included

- Source management, resumable scanning, folder-tree browsing, and portable
  app data remain offline-first and do not rewrite media files during scans.
- TagLib-backed metadata reads cover the supported container families,
  configurable multi-value separators, compilation/disc/release grouping,
  database-only edits, atomic write-back, exact undo, artwork replacement,
  centered square crop, removal, and unknown-tag preservation.
- Metadata matching is opt-in, cached locally, rate-limited, attributed, and
  only previews changes until the user applies them.
- Albums, artists, and genres use a virtualized cover gallery. Per-view sort,
  density, cover size, quick filters, column visibility/order, recent/history
  views, multi-select, drag/drop, disc totals, and ReplayGain indicators are
  persisted in settings.
- Search supports quoted phrases, exclusions, scoped fields, numeric
  comparisons, playlist membership, diacritics, CJK/RTL text, suggestions,
  recent history, clear-history, and result highlighting.
- Playlists support manual and smart CRUD, nested AND/OR rules, validation,
  local preview counts, ordered editing, queue/collection integration,
  M3U8/PLS/XSPF interchange with missing-location reporting, queue save,
  undo/redo, automatic backups, and portable relative paths.

## Verification

The release configuration builds cleanly and the test suite passes with 324
tests. The metadata/search suite includes supported-format, atomic-edit,
Unicode, large-result, configurable-separator, and nested smart-rule cases.

The next roadmap area is Milestone 5 playback/queue controls; lyrics are
already integrated and can be refined there without changing the library
index or user media files.
