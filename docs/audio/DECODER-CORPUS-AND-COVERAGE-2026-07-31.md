# Decoder corpus and installed-codec validation

Date: 2026-07-31

This report is the evidence for DEC-001 through DEC-004. All fixtures are
generated from a two-second, 997 Hz sine wave by
`scripts/New-AudioFormatCorpus.ps1`. They contain no copyrighted music.

## Coverage

| Format | Extensions | Playback path | Installation behavior |
|---|---|---|---|
| PCM WAV/AIFF | `.wav`, `.wave`, `.aif`, `.aiff` | NAudio readers | Bundled |
| MP3 | `.mp3` | NAudio MP3 frame decoder | Embedded probe decoded at runtime |
| FLAC | `.flac` | BunLabs managed FLAC | Bundled |
| Vorbis | `.ogg` | NVorbis | Bundled |
| Opus | `.opus` | Concentus | Bundled; encoder pre-skip and bounded-memory seeking handled by `OpusWaveStream` |
| AAC | `.aac`, `.m4a`, `.mp4` | Windows Media Foundation | ADTS and MP4 probes decoded at runtime |
| ALAC | `.m4a`, `.mp4` | Windows Media Foundation | Embedded probe decoded at runtime |
| WMA | `.wma` | Windows Media Foundation | Embedded probe decoded at runtime |
| DSD | `.dsf`, `.dff` | Native parser and Apache DST decoder to DoP 1.1 | Bundled for DSF plus uncompressed or DST-compressed DFF on win-x64 |

`AudioDecoderCapabilityService` never assumes that a Media Foundation
transform exists. It extracts only embedded synthetic probes to a unique
temporary path, verifies that the real playback factory produces PCM, and
removes the temporary file. It never reads or writes the user's library or
music. Settings displays these results, and diagnostics exports them as
`decoder-capabilities.json`.

## Adversarial and behavioral fixtures

The committed corpus includes:

- deterministic references for every advertised codec and container;
- a VBR MP3;
- an ALAC file with a Unicode filename;
- a FLAC with a Unicode title, multi-artist syntax, a 12 KB comment, and an
  embedded 64×64 cover;
- a malformed FLAC header and a truncated MP3;
- minimal legal DSF and DFF streams.

Tests hash every generated fixture before use. Metadata reads are verified to
leave the media bytes unchanged. Every PCM decoder is opened, read, moved to
the middle, read again, drained to EOF, and read once more to prove stable EOF.
DSF and uncompressed DFF are rewound and reread, including DoP marker validation. DST-compressed DFF has generated container/error tests plus a separate opt-in, bit-exact qualification against external compressed frames that are not redistributed with this repository. Opus has a
sample-level sequential-versus-seek comparison with a maximum eight-LSB
tolerance to account for decoder preroll state.

## Reproduction

Regenerate fixtures when `ffmpeg` is available:

```powershell
./scripts/New-AudioFormatCorpus.ps1
```

Run the qualification tests:

```powershell
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter FullyQualifiedName~AudioFormatCorpusTests
```

Result on the 2026-07-31 development machine: 18/18 decoder-corpus tests
passed, including all 10 installed codec paths. The focused diagnostics and
Settings smoke set passed 20/20.

## Remaining boundaries

CUE sheets and chapters are tracked by DEC-005 and DEC-006. Physical output qualification
is tracked by AUD-006 and HW-001 through HW-004; decoder success alone does not
claim bit-perfect hardware output.
