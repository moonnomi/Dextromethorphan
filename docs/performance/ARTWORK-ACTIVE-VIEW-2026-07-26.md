# Active-view artwork resolution

ART-007 removes the library-refresh fan-out that previously started artwork extraction for albums, artists, genres, folders, and playlists together.

## Resolution scopes

| Visible surface | Cards eligible for artwork resolution |
|---|---|
| Albums, Artists, Genres | Only cards currently exposed by gallery paging |
| Collection detail | Only the selected collection |
| Folders, Playlists | Only the active sidebar collection |
| Songs, Favorites, Now Playing | No collection-card resolution |

Changing tabs, changing search results, opening or closing a collection, and restoring Mouse4/Mouse5 navigation now cancel the previous scope and create a new active-view scope. Loading another gallery page restarts the scope over the exposed page window; cards that already have artwork are filtered out before work is scheduled.

Current-track and queue artwork remain independent high-priority paths and are not canceled by library navigation.

## Verification

- Nine planner cases verify gallery, sidebar, detail, track, and playback scopes.
- The complete Release suite passes: 52 tests.
- An instrumented 10k-track run started with 28 planned Album cards, then planned 28 Artist cards, 20 Genre cards, zero Song cards, 500 active Folder cards, and 20 active Playlist cards as the benchmark navigated those views.
- Album paging expanded only the active Album scope in 28-card increments.
- The benchmark process completed successfully and exited with code 0.

The run was used to validate scheduling scope, not to replace the clean-machine PERF-006 reference baseline.
