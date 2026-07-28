# Artwork format detection and safety

ART-010 validates artwork before it reaches WPF and preserves the detected source format in new cache filenames.

## Behavior

- New embedded artwork is identified by file signature and stored as `.jpg`, `.png`, `.gif`, `.bmp`, `.tiff`, or `.webp`.
- Existing `.art` cache entries and database paths remain readable; no destructive migration is required.
- Container parsing extracts dimensions with bounded reads and rejects:
  - empty or missing files;
  - unsupported signatures;
  - truncated or structurally invalid containers;
  - encoded files larger than 32 MB;
  - either dimension above 16,384 pixels;
  - decoded source geometry above 64 megapixels.
- Persistent thumbnail generation performs the same inspection before constructing a WPF bitmap.
- WPF decoding remains size-bounded and rejects an invalid decoded surface before it can enter the memory cache.
- Rejected sources do not fall back to an unsafe original decode. They enter the existing five-minute failure suppression and leave the stable artwork placeholder visible.
- Diagnostics record `thumbnail.source-rejected` with the format, reason, encoded size, and decoded dimensions where available. The performance overlay includes the rejection count.

PNG chunks, JPEG markers, GIF trailers, BMP headers, TIFF IFD dimensions, and the VP8/VP8L/VP8X WebP dimension layouts are parsed without invoking a full image decoder.

## Verification

- Tests cover five WIC-encoded formats and assert both detected extension and exact dimensions.
- Focused tests cover unknown data, truncated PNG data, over-limit dimensions, a sparse file over the encoded-size limit, extension-aware cache storage, corrupt cache rejection, and legacy `.art` lookup.
- An integration test proves a dimension bomb is rejected with zero WPF source decodes and no persistent variant output.
- The complete Release suite passes: 77 tests.
- A fresh 10k-track fixture recorded:
  - 1,658.2 ms cold process-to-interactive;
  - 20.7 ms median scroll p95 and 35.3 ms worst frame;
  - zero scroll frames over 50 ms;
  - zero artwork rejections or decode errors for the valid fixture.
- Inspection plus requested-variant generation remained off-thread at 4.9 ms average and 8.7 ms p95 on the cold run.

Format-specific visual fallback and user-facing cache repair remain part of ART-011 and ART-012.
