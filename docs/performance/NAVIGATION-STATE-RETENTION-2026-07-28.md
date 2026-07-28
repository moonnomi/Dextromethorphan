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
- 86 tests pass, including independent offset/count storage and invalid-state normalization.
- The implementation suppresses scroll capture while a navigation restore is pending, preventing WPF's transient zero offset from overwriting the saved position.

VIEW-002 intentionally leaves presentation collection reuse to VIEW-003 and range-based track replacement to VIEW-004.
