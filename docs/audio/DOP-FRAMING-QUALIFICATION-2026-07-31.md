# DoP framing qualification — 2026-07-31

This report records the hardware-independent portion of HW-003. It proves the bytes and carrier formats Dextromethorphan will submit to WASAPI; it does not claim that a physical DAC interpreted them as DSD.

## Generated coverage

All source bytes are generated in memory and written only to unique temporary test files. No music-library, artwork-cache, settings, or database path is opened.

| Container/path | Rate and channels | Evidence |
|---|---|---|
| DSF | DSD64 stereo | Payload order, `0x05`/`0xFA` marker parity, block callbacks, and seek |
| DSF | DSD128 stereo | 352.8 kHz/24-bit carrier, payload order, marker parity, callbacks, and seek |
| Uncompressed DFF | DSD128 six-channel | MSB-first reversal, byte interleave across all channels, marker parity, callbacks, and seek |
| DST DFF escape frame | DSD128 stereo | Native decoder output, 352.8 kHz carrier, full DoP bytes, and intra-frame seek |

Every read is asserted to return a whole WASAPI frame even when the requested callback size is deliberately not block-aligned. Seeking compares the returned byte slice against an independently constructed expected DoP stream and checks the resulting stream position.

## Negotiation discovery

Exclusive capability discovery now checks PCM carriers through 768 kHz, including the DoP-relevant 176.4, 352.8, and 705.6 kHz rates. A newly discovered format is also exercised by the opt-in silence playback matrix rather than merely displayed.

The current Realtek onboard endpoint was re-probed after this change. It accepted and played its same 24 formats through 192 kHz, rejected every candidate above 192 kHz, and left endpoint volume unchanged. That is a correct explicit rejection, not evidence for DSD128 hardware support.

## Remaining physical gate

With a compatible DAC attached, run `scripts/Test-AudioHardware.ps1` and then play generated/legally supplied DSD64 and DSD128 files in exclusive, source-matched, fixed-unity DoP mode. HW-003 closes only when diagnostics show 176.4/352.8 kHz without fallback and the DAC itself indicates the expected DSD rate before and after repeated seeks.
