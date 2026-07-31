# DST-compressed DFF qualification — 2026-07-31

This report closes DEC-007 for the primary win-x64 target. Dextromethorphan now parses the DSDIFF `DST ` sound-data container, validates `FRTE`, indexes `DSTF` frames, losslessly decodes each frame to native DSD, and feeds the existing direct DoP 1.1 framer without PCM conversion.

## Decoder and distribution

- Decoder: `dst-decoder` 0.1.2, Apache-2.0.
- Integration: a small Rust `cdylib` with a versioned four-function C ABI and managed error translation.
- Locked crate checksum: `0d22ef63dfde0eac89eca20926f8bb64eec4bf12016647274a51d3bb11f6147c`.
- Qualified win-x64 DLL SHA-256: `EAB55765359846D76DD0549B1E88681D702019EFAE72B1E27C6903328672F3FD`.
- Release output includes the Apache license and attribution notice under `licenses`.
- A reproducible locked build is available through `scripts/build-dst-decoder.ps1`; normal users do not need Rust because the qualified DLL is checked in.

The FFI boundary catches Rust unwinds and converts them to `InvalidDataException`. Deterministic malformed-frame fuzz verifies that corrupted input cannot escape the managed decoder contract as an unrelated exception.

## Streaming and seeking

Only the current 1/75-second DST frame is decompressed and cached. File reads, memory, and decode time therefore remain bounded independently of track duration. Seeking maps the requested DoP frame to its containing `DSTF`, decodes that frame once, and starts at the exact intra-frame offset. DoP marker parity is derived from the global output frame, so it remains continuous across DST frame boundaries and after seeking.

Container validation rejects conflicting DSD/DST sound chunks, invalid or duplicate frame information, unsupported frame rates, mismatched frame counts, oversized frames, invalid padding, malformed CRC chunks, and truncated arithmetic data with the failing frame number in the message.

## Qualification evidence

Always-on tests use generated containers and malformed payloads. They cover native API discovery, invalid configuration/error propagation, deterministic malformed-frame fuzz, frame-count mismatch, delayed payload failure, legacy uncompressed DFF behavior, and installed-capability reporting.

The opt-in external test used three consecutive stereo DSD64 compressed frames and independently decoded reference DSD bytes. It verified:

- all three native DSD outputs byte-for-byte;
- the complete cross-frame DoP byte stream and alternating markers;
- a seek into the middle of the second DST frame;
- 150 repeated decodes within a two-second real-time guard;
- unchanged SHA-256 hashes and timestamps for every external fixture.

The SACD-derived external bytes are deliberately not committed or redistributed. To repeat the qualification with legally supplied `frame_001..003.dst` and matching `.dsd` references:

```powershell
$env:DEXTROMETHORPHAN_DST_FIXTURES = 'C:\path\to\stereo'
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter FullyQualifiedName~DstDecoderQualificationTests
```

Decoder qualification does not prove what a physical DAC reports. DoP negotiation and DAC indication remain HW-003.
