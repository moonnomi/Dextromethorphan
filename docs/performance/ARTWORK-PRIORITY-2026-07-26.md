# Prioritized artwork scheduling

ART-005 replaces fire-and-forget thumbnail decoding with a bounded, priority-aware queue.

## Behavior

- The current-track cover and selected collection artwork use immediate priority.
- Realized gallery cards, sidebar cards, and queue entries use visible priority.
- Decorative artwork such as the blurred Now Playing background remains deferred.
- Only two bitmap decodes can run concurrently, preventing rapid scrolling from flooding the thread pool and disk.
- Requests for the same path and size share one decode. A later immediate consumer promotes queued work instead of starting a duplicate.
- When virtualization unloads a card, its waiter is canceled. If nobody else needs the queued image, it is removed before file access or bitmap decode.
- Collapsing a view cancels unfinished work but retains already-decoded frozen images, keeping cached tab returns cheap.
- The strong LRU cache and five-minute failure suppression from ART-003/004 remain in effect.

The developer performance overlay now separates active decodes from queued work and reports how many stale requests were dropped before decoding.

## Verification

- Three scheduler tests cover priority ordering, stale cancellation, request promotion, and deduplication.
- The complete Release test suite passes: 43 tests.
- A benchmark process exits cleanly after host disposal instead of retaining decoder workers.
- A final 10k-track warm sample recorded a 22.615 ms album-scroll p95, a 26.431 ms worst frame, and no frame over 50 ms.

The full automated regression run was also exercised. It correctly rejected a sample captured while the machine was under unrelated foreground load, so that sample was not promoted to a reference baseline. This is intentional: ART-005 does not weaken the PERF-006 tolerances or replace the stored clean-machine references.
