# Generated audio format corpus

Run `scripts/New-AudioFormatCorpus.ps1` to regenerate these files from a two-second synthetic 997 Hz sine wave. The corpus contains no copyrighted recording. `manifest.json` records hashes and the generator version.

`metadata-heavy.flac` carries a generated cover, Unicode text, multi-artist separators, and a 12 KB comment. `reference.mp3` uses VBR encoding. These exercise metadata and duration edge cases without relying on third-party media.

The malformed and truncated files are intentional negative fixtures. The tiny DSF/DFF files contain deterministic test bytes for container, marker, interleave, and seeking tests; they are not listening material.
