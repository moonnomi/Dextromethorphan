# DAC qualification matrix

This is the release checklist for a physical USB DAC. It separates safe automated negotiation/playback checks from tests that require a person to observe or change hardware. A row is evidence only when its report or notes identify the device model, driver version, connection, and result.

## Automated, non-destructive gate

Set the DAC as the Windows multimedia default, close other applications using it, and run:

```powershell
./scripts/Test-AudioHardware.ps1
```

An exact Windows endpoint ID can instead be supplied with `-DeviceId`. The script outputs finite in-memory silence, probes only driver-accepted exclusive formats, records 2/10/100 ms buffer results, and verifies that endpoint volume did not change. It does not open the music library, artwork cache, or library database.

| Area | Required cases | Pass condition |
|---|---|---|
| Shared PCM | 44.1/48/88.2/96/176.4/192 kHz × 16/24/32-bit × mono/stereo | Every row completes; unsupported behavior is explicit |
| Exclusive PCM | Every format accepted by `IsFormatSupported` | Initialization, callbacks, and clean end all succeed |
| Buffer boundaries | 2, 10, and 100 ms | No initialization failure or stalled completion |
| Volume safety | Entire automated run | Endpoint scalar is bit-for-bit unchanged |
| Privacy | Generated JSON report | Endpoint ID is redacted; no library paths are present |

## Manual device tests

| Area | Procedure | Evidence / pass condition |
|---|---|---|
| Rate switching | Play one known file at each supported rate in exclusive/source-matched mode | DAC display and diagnostics agree with the source; no shared fallback |
| Bit depth | Play known 16/24/32-bit fixtures | Requested/effective diagnostics match or show an explicit, expected rejection |
| Exclusive contention | Hold the endpoint from another exclusive client, then start playback | Clear error/fallback reason; UI remains responsive |
| Hot unplug/replug | Unplug during playback, wait through recovery, reconnect | No freeze or volume write; recovery/fallback state is visible; playback can resume |
| Default-device change | Switch Windows default during shared playback | Configured device/fallback policy is followed without silent rerouting |
| Sleep/resume | Sleep while playing, then resume | Endpoint is reacquired; position and queue remain coherent |
| Explicit hardware volume | Record the scalar, opt into Hardware volume, adjust once, then restore | Only the explicit action changes the endpoint; original value is restored manually |
| DoP DSD64/128 | Play legal DSF and uncompressed DFF fixtures through a DoP profile | DAC reports DSD64/128; diagnostics report direct DoP; no DSP is active |
| DoP seek | Seek repeatedly on both channel layouts | Marker alternation/channel interleave remain valid and the DAC stays in DSD mode |
| Transition soak | Mixed-rate gapless/crossfade queue for 2 hours | No stalls, unexpected reopen loops, audible boundary defect, or memory trend |
| Release soak | Representative queue for 8 hours | Zero unhandled errors; underruns and memory growth are recorded and accepted |

HW-003 closes only after the DoP rows pass on physical hardware. HW-004 closes only after the eight-hour run completes and its diagnostics are retained.

Generated DSD64/128 framing, multichannel interleave, callback alignment, seeking, and high-rate carrier discovery are already covered by the [DoP framing qualification](DOP-FRAMING-QUALIFICATION-2026-07-31.md). The manual rows must confirm that a real driver accepts those carriers and that the DAC actually indicates DSD rather than PCM.

The automated eight-hour shared-mode transition run is documented in [audio soak qualification](AUDIO-SOAK-QUALIFICATION-2026-07-31.md). It complements, but does not replace, the physical-DAC rows above.
