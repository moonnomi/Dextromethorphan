# Gapless and crossfade qualification

Date: 2026-07-31

This report is the evidence for DSP-001 and DSP-002.

## Corrections

The transition provider now fills short decoder reads before treating them as
an end-of-stream boundary. This prevents callback-sized reads from creating a
false track boundary, losing samples, or inserting silence.

Crossfades use equal-power cosine/sine gains with exact outgoing and incoming
endpoints. The effective fade is clamped to both tracks. A very short incoming
track therefore shortens the fade instead of making the outgoing track skip
its remaining tail.

The normalization path now handles integer PCM at 8, 16, 24, and 32 bits and
IEEE float at 32 and 64 bits. WAV extensible subtypes are read directly from
the `fmt ` chunk because NAudio exposes some valid 24-bit extensible files as
the base `WaveFormat` type. This fixes crossfades and processed playback for
common 24-bit WAV files while retaining the original float DSP pipeline.

## Legal fixtures and tests

The generated, copyright-free corpus includes:

- a 44.1 kHz mono 16-bit constant-level WAV;
- a shorter 48 kHz stereo 24-bit extensible WAV;
- deterministic in-memory stereo streams that deliberately return short
  decoder chunks.

Automated qualification verifies:

- exact sample-for-sample gapless concatenation with no loss or duplication;
- no silence at the join;
- equal-power crossfade endpoints and timing;
- preservation of an outgoing tail when the incoming track is very short;
- real-file sample-rate, channel-count, and bit-depth normalization before a
  crossfade.

```powershell
./scripts/New-AudioFormatCorpus.ps1
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter FullyQualifiedName~DspQualificationTests
```

Focused result on 2026-07-31: 4/4 transition qualification cases passed.
