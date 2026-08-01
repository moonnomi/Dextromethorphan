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

The repository now includes a deliberately guarded physical runner. Listing endpoints is read-only and does not require a DAC acknowledgement:

```powershell
./scripts/Test-DopHardware.ps1 -ListDevices
```

After connecting a compatible DAC, copy its exact endpoint ID and run:

```powershell
./scripts/Test-DopHardware.ps1 `
  -ConfirmCompatibleDac `
  -DeviceId '<exact endpoint ID>' `
  -DacModel '<manufacturer and model>' `
  -DriverVersion '<installed driver version>' `
  -Connection '<USB port/connection>' `
  -Dsd64Indication Pass `
  -Dsd128Indication Pass
```

The runner refuses the mutable Windows default endpoint, generates finite DSD64 and DSD128 silence outside the music library, pins one exact endpoint for the whole run, seeks twice per rate, and requires direct event-driven exclusive 176.4/352.8 kHz 24-bit carriers without fallback. It hashes the endpoint ID in the report and requires bit-identical endpoint volume before and after. A report exits successfully only when both automated cases, traceable hardware metadata, and both operator-observed DAC indications pass. Unknown or failed indications cannot close HW-003.

No compatible DAC is currently attached, so this guarded runner has only been safety-tested in list/refusal modes. HW-003 closes only when its retained physical report says `HardwareQualified: true`; generated framing evidence alone remains insufficient.
