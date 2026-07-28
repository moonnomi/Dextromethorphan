# Artwork dispatcher batching

ART-008 replaces per-card dispatcher continuations with a shared, bounded artwork property-update batcher.

## Behavior

- Decode tasks no longer capture the WPF synchronization context.
- Frozen `BitmapSource` assignments, resolved card artwork paths, and queue-entry artwork paths share one update queue.
- The queue waits 12 ms to coalesce nearby completions, roughly one 60 Hz frame.
- Each background-priority dispatcher turn applies at most 12 updates, yielding between batches when more work remains.
- Updates tied to a canceled tab, query, queue, or library generation are dropped before touching a binding.
- A failed individual property update is recorded and does not discard the rest of its batch.
- Shutdown drains queued updates without retaining view models or image controls.

The developer overlay reports pending artwork UI updates and completed batch count alongside decoder activity. Diagnostic summaries record `artwork.property-update-batch` duration and failures.

## Verification

- Three focused tests cover bounded batch size, canceled-generation filtering, concurrent producers, single scheduling, and exactly-once application.
- The complete Release suite passes: 55 tests.
- A verbose 10k-track run applied 406 property updates in 35 dispatcher batches:
  - 33 batches contained the maximum 12 updates;
  - one contained 8 updates and one contained 2;
  - average batch size was 11.6 updates;
  - every observed batch contained more than one update.
- Normal diagnostics measured the 35 batch flushes at 0.067 ms average, 0.35 ms p95, and 0.996 ms maximum with zero errors.
- The same run decoded 195 thumbnails and produced no album-scroll frame over 50 ms.

This verifies coalescing itself. It does not replace the clean-machine PERF-006 reference baseline.
