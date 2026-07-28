# Navigation state retention

VIEW-002 gives every primary tab and collection detail its own presentation state.

## What changed

- Albums, Artists, Genres, Folders, and Playlists remember their selected card by stable kind/key identity.
- Songs, Favorites, sidebar collections, and collection details remember the selected track by path.
- Gallery, sidebar, and track-list vertical offsets are stored under independent navigation keys.
- A gallery also remembers how many incremental pages were materialized, so a restored offset cannot be clamped to the first page.
- Scroll restoration is deferred until WPF has applied the target view and completed layout.
- Mouse4/Mouse5 history uses the same keys, so returning through history restores the prior view rather than inheriting state from the tab that was just left.

## Verification

- `dotnet test Dextromethorphan.slnx -c Release --no-restore`
- 88 tests pass, including independent offset/count storage and invalid-state normalization.
- The implementation suppresses scroll capture while a navigation restore is pending, preventing WPF's transient zero offset from overwriting the saved position.

The automated 10k-track WPF benchmark now performs a real Albums → Artists → Back → Forward sequence and verifies state after layout:

| Check | Result |
|---|---:|
| Original gallery collection identity reused | Pass |
| Selected album restored | Pass |
| Materialized cards restored | 140 / 140 |
| Vertical offset restored | 721.5 / 721.5 px |
| Back latency | 121.499 ms |
| Forward latency | 71.531 ms |

The benchmark result is included in every raw performance report and summarized by `Measure-PerformanceBaseline.ps1`; `Test-PerformanceGates.ps1` fails when any run loses navigation state.

VIEW-002 intentionally leaves presentation collection reuse to VIEW-003 and range-based track replacement to VIEW-004.
