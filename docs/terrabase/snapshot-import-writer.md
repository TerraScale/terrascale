# Snapshot reception and erasure recovery

This updates the import scheduling described in [epoch recovery](erasure-epoch-recovery.md).
Receive and validate every ordered page, the consistent collection floor, the terminal vector and clean EOF before
opening the receiver's durable transaction. A private temporary spool in the data directory admits 64 MiB of page working memory through
the existing memory budget, at most 65,536 frames and 1 GiB of encoded data. The file has owner-only permissions and
is immediately unlinked on Unix; disposal, cancellation and process death release it. Excess capacity refuses the import.

The transaction still checks its destination incarnation, joins the entire baseline, provisionally reconciles collected
absence, proves every forgotten SYSTEM state's authors against the terminal cuts (TS-1012), and records coverage and
the collection floor with one commit. Neither interrupted reception nor failed proof publishes a partial snapshot.
Applying and reconciling a large file still requires local storage work under the writer; there is no one-second bound
for arbitrarily large snapshots. Observe `terrabase.storage.writer_held` when measuring that remaining work.

Retired epochs remain pending until their original auth and SYSTEM cuts are actually covered. A missing original cut,
or a marker no witness retained, cannot be reconstructed from unchanged peer snapshots. No-progress imports do not
re-fence. Per-erasure, per-peer retries back off from one minute to one hour (failed transfers start at ten seconds);
changed old-cut evidence or a new epoch retries immediately. The bounded in-memory schedule resets on restart and
never counts as coverage. The existing epoch-stalled alert remains, with `epoch_no_progress` error counts.
Imported prefix proofs preserve peer liveness timestamps, so they do not revive retired epochs and pin stable progress.
There is no API, receipt, persistent storage-format or erasure-proof change.
