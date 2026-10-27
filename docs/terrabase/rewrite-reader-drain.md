# Healthy rewrite reader admission

A healthy rewrite stops admitting pooled readers before its last off-writer drain.
Existing readers finish while the writer remains available. New readers wait asynchronously
until the rewrite completes or refuses, then rent against the current file. Cancellation
still ends an individual wait. Replay uses its own tracked connection; writes continue
during checkpoint and catch-up.

This closes the interval in which a new reader could enter after the drain, then wait
for the writer held by the rewrite. The 250 ms writer-held drain and 750 ms final budget
remain bounded. Safety refusals remain failures; no retry category was broadened.
The Fast regression coordinates a late reader and a concurrent write at the final
writer boundary, checking both successful replacement and refusal cleanup. The Slow
rolling-restart scenario remains enabled (issue #718).

There is no API, on-disk format, authentication or credential change.
