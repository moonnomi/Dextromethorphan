# Output profiles and endpoint recovery — 2026-07-31

This update completes the consumer-facing output-profile and endpoint-resilience work for Milestone 3.

## Per-output control

Settings now edits one persistent profile per Windows endpoint:

- shared or exclusive event-driven WASAPI;
- 2–1,000 ms event buffer;
- source-matched, endpoint-mix, or fixed sample rate;
- source-matched or fixed 16/24/32-bit PCM;
- reject non-stereo, downmix to stereo, or source-channel policy;
- no fallback, same-device shared fallback, or system-default shared fallback;
- software, explicit endpoint hardware, or fixed-unity volume;
- disabled, DoP, or native DSD selection;
- direct-path preference and per-output crossfade.

The editor queries and displays the endpoint mix format and accepted mono/stereo exclusive formats before Save is available. Bluetooth/headset and HDMI/display endpoints receive conservative shared-mode defaults; ordinary non-default endpoints receive an exclusive/source-matched starting profile.

## Failure and diagnostic behavior

Core Audio endpoint notifications are forwarded into the app. Removal, state change, and default-device change affecting the active profile trigger bounded recovery attempts at 200, 500, 1,000, and 2,000 ms while preserving the current position. An explicitly configured fallback is reported as fallback rather than bit-perfect playback.

The live diagnostics panel and exported support bundle include requested/effective mode and endpoint, source/output format, decoder, direct/DSP reason, fallback reason, recovery attempts, underruns, and last/maximum provider callback duration. Device identifiers remain hashed in support bundles.

Normal discovery and capability probing never writes endpoint volume. Hardware volume is touched only when the user explicitly saves `Hardware` for that endpoint.

## Qualification evidence

- The automated suite covers profile round-trip, fallback defaults, recovery backoff, endpoint notifications, the complete PCM conversion matrix, multichannel downmix, diagnostics export, and Settings XAML loading.
- The opt-in hardware probe passed on `Speakers (Realtek(R) Audio)`.
- Its shared mix is 48 kHz, 32-bit, stereo.
- The driver accepted all tested 44.1–192 kHz mono/stereo PCM combinations at 16 and 24 bits, rejected the tested 32-bit PCM/float combinations, and exposed event-driven exclusive mode.
- The Windows endpoint volume value was identical before and after discovery/capability probing.

Playback qualification now generates finite silence through the real endpoint rather than relying only on capability flags. The current onboard endpoint passed all 36 shared-mode PCM combinations, all 24 exclusive combinations accepted by its driver, and 2/10/100 ms shared buffer checks without changing endpoint volume. See [PCM output qualification](PCM-OUTPUT-QUALIFICATION-2026-07-31.md). Physical-DAC, DoP, and long-soak work remains separately gated by HW-002 through HW-004.
