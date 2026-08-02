# Lyrics

Dextromethorphan supports plain text, standard LRC, enhanced LRC word timing, simultaneous lyric lines, translations, romanization, and instrumental gaps. The Now Playing lyric panel follows the playback engine's authoritative timeline, so seeking and speed changes update the highlighted line and words immediately.

## Local sources

Lyrics are resolved without changing the audio file. The order is:

1. the alternate file previously chosen for that track;
2. an exact-name `.lrc`, then an exact-name `.txt` beside the track;
3. language variants such as `track.ja.lrc`;
4. files matching the title or `artist - title`;
5. lyrics embedded in the track metadata;
6. a previously confirmed online result, when online lookup is enabled.

Use the controls above the lyric panel to reload sources, choose an alternate LRC/TXT file, edit or create a UTF-8 sidecar, or remove a local sidecar. Removing lyrics never edits or deletes the audio file. The per-track offset slider is persisted in settings and can compensate for lyrics that consistently run early or late.

## Display and timing

The Lyrics settings page controls automatic/static/synced display, font size, alignment, line spacing, artwork blur, and karaoke word animation. Disabling app or Windows animations keeps the timing accurate while replacing continuous word fills and scrolling motion with immediate updates.

Supported timing includes `mm:ss`, hour-qualified timestamps, dot or colon fractions, multiple timestamps on one line, and enhanced word tags. Multiple lines with the same timestamp remain active together. Empty timed lines are shown as instrumental gaps. Prefix a timed line with `[tr]` or `[translation]` for a translation, `[rom]` or `[romanization]` for romanization, and `[inst]` or `[instrumental]` for an explicit instrumental line.

The parser rejects content over 2 MB, bounds line length and line count, ignores malformed timing instead of failing playback, and accepts UTF-8, UTF-16, UTF-32 BOMs plus common legacy text encodings.

## Optional LRCLIB lookup

Online lookup is off by default. Enable **Manual LRCLIB searches** in Lyrics settings, then press the globe in Now Playing. Only that explicit action sends the current track's title, artist, album, and duration to LRCLIB. Requests are sequential and rate-limited, `Retry-After` responses are honored, and search results are cached under `%APPDATA%\Dextromethorphan\lyrics` for 30 days.

An online result is not applied until it is selected and confirmed. The source is credited as LRCLIB in the player. A confirmed choice can be restored from the local cache without another request. Disabling the provider returns the player to fully offline lyric resolution.

LRCLIB lookup does not publish lyrics, write tags, or alter music files.
