# Preferred external artwork

ART-014 makes folder-based cover discovery explicit and deterministic.

## Selection

For media files in a directory, Dextromethorphan now considers exact, case-insensitive base names in this order:

1. `cover`
2. `folder`
3. `front`
4. `album`
5. `albumart`

Within the same base name, the extension order is `.jpg`, `.jpeg`, `.png`, `.webp`, `.tif`, `.tiff`, `.bmp`, then `.gif`. A final ordinal path comparison makes the result stable even on unusual case-sensitive Windows volumes.

Every candidate passes the same encoded-size, image-structure, dimension, and decompression-bomb checks as embedded artwork. A corrupt higher-priority file is skipped so the next valid candidate can be used.

## Library behavior

- External artwork takes precedence over embedded artwork during scans and lazy artwork lookup.
- A scan resolves the preferred file once per directory, not once per track.
- The SQLite file index carries the current artwork path, allowing an unchanged audio file to adopt a newly added preferred cover without reparsing its tags.
- If a previously selected external file disappears, the affected track is reparsed so embedded artwork or the next preferred external file can take over.
- External files remain in place; generated display sizes continue to use the versioned persistent-thumbnail cache.

## Verification

- Automated coverage proves name precedence (`cover` over `folder`), extension precedence (`.jpg` over `.png`), corrupt-candidate fallback, unchanged-media adoption, and fallback to embedded artwork after an external file disappears.
- The full Release suite remains the acceptance check for embedded-artwork caching and invalidation behavior.
