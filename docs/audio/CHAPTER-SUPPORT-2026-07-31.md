# Embedded chapter support

Date: 2026-07-31

This report is the evidence for DEC-006.

## Supported metadata

| Container/tag | Chapter representation | Reader |
|---|---|---|
| MP3 / ID3v2 | `CHAP` frames with optional `TIT2` subframes | TagLibSharp chapter frames |
| M4A / MP4 / M4B | Nero-style `chpl` atom | Bounded native MP4 atom reader |
| FLAC / Ogg-style Xiph tags | `CHAPTERnnn` and `CHAPTERnnnNAME` | TagLibSharp Xiph comments |

Chapters are normalized into ordered title/start/end records. Explicit valid
ends are retained; otherwise the next start or media duration supplies the
end. Duplicate starts, invalid ranges, and starts outside the track are
discarded. Missing titles receive a numbered fallback. Malformed optional MP4
chapter data cannot prevent the track itself from importing.

Schema migration 6 persists chapters as JSON in the local library index. As
with every database migration, an existing database is backed up before the
transaction and restored if migration fails. No media file is written.

CUE segments receive only the embedded chapters that intersect their segment;
timestamps are translated to the segment's own playback timeline.

## User interaction

The main seek bar renders lightweight chapter ticks without creating one WPF
control per marker. Right-clicking the seek bar opens the app-themed chapter
list with timestamps; selecting a chapter seeks through the same bounded seek
path used by lyrics and normal pointer seeking.

## Fixtures and qualification

The generated legal corpus now includes the same two chapters in synthetic
MP3, M4A, and FLAC files through all three metadata schemes. Tests verify the
titles and 0/750/2000 ms boundaries and a SQLite write/read round trip.

```powershell
./scripts/New-AudioFormatCorpus.ps1
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter "FullyQualifiedName~ChapterTests|FullyQualifiedName~AudioFormatCorpusTests"
```

Focused result on 2026-07-31: all chapter/corpus/UI-smoke cases passed (23/23).
