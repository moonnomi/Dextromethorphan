# Artwork loading states and retry

ART-011 turns asynchronous artwork loading into an explicit, bounded state machine.

## Behavior

- Every asynchronous image exposes `Empty`, `Loading`, `Retrying`, `Loaded`, `FailedTransient`, and `FailedPermanent` states.
- Existing initial, note, and surface placeholders remain in their fixed-size containers while loading, retrying, or failed. Image completion never changes card measurement or layout.
- Loaded artwork fades from 0 to 100% opacity over 160 ms with an ease-out curve.
- Fade-in is disabled when Windows client animations are disabled or the app's Animations setting is off.
- Missing, locked, and temporarily inaccessible files use bounded retries at approximately 0.4, 1.5, and 5 seconds.
- Unsupported, malformed, or corrupt artwork enters permanent failure immediately and is not sent through the decoder again during the process.
- A recycled, hidden, unloaded, or rebound image cancels its retry delay and stale property update.
- Attached `State` and `FailureReason` properties make the behavior available to future themed placeholder and repair controls without changing image layout.

Permanent means permanent for the current source identity and process. Source-change invalidation remains tracked by ART-013.

## Verification

- Policy tests cover transient/permanent classification, increasing bounded backoff, permanent decode suppression, and recovery when a missing file appears after its retry window.
- The complete Release suite passes: 81 tests.
- A warmed 10k-track fixture recorded:
  - 1,711.0 ms cold process-to-interactive;
  - 19.0 ms median scroll p95 and 41.3 ms worst frame;
  - zero scroll frames over 50 ms;
  - 4.20% median idle CPU;
  - zero artwork rejection or decode errors.
- Dispatcher batches containing artwork assignments and state transitions remained at 0.13 ms average, 0.53 ms p95, and 2.15 ms maximum.

The opacity animation is deliberately short and limited to realized images; it does not retain off-screen containers or start a separate rendering loop.
