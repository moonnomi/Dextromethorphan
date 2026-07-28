# Large-library memory follow-up

This follow-up keeps the 50k-track library inside the 300 MiB release budget without weakening artwork virtualization or loading every playlist.

## Changes

- Album-grid cards request a dedicated 192-pixel persistent thumbnail instead of retaining a 256-pixel surface for every visible card.
- The decoded-artwork LRU has a 16 MiB default budget. This holds more than a viewport of 192-pixel cards while allowing hidden surfaces to leave memory promptly.
- Repeated artist, album, genre, codec, and artwork-path strings are shared within each SQLite result set.
- Entering Playlists loads summary rows only. Full tracks remain lazy until a playlist is explicitly selected.
- Background artwork resolution and diagnostic writes expose an idle wait used by the benchmark harness.
- View-transition animation clocks stop after completion rather than retaining their final values indefinitely.
- Performance reports capture working set after startup, navigation, and scrolling in addition to the process peak.

## Verification

- The Release test suite passes with coverage for repeated metadata-string sharing and all five thumbnail tiers.
- A 50k-track warm benchmark recorded:
  - 2,226 ms process-to-interactive;
  - 75.0 ms maximum cached tab switch;
  - 15.6 ms p95 and 26.0 ms worst album-scroll frame;
  - zero frames over 50 ms;
  - 297.9 MiB peak working set.

The benchmark process reported 5.9% idle CPU after exercising every workload. A separately settled normal app process measured 0.27%, so benchmark teardown/settling remains a release-gate harness issue rather than a persistent app workload.
