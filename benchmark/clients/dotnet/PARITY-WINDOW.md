# Parity timing window

`ParityWindow` is separate from the existing scenario driver. The live plan is
2 seconds of warmup, 5 seconds of measurement and a 30-second drain, at the
caller's concurrency. Every operation started inside the measurement window
retains its complete latency through the drain. Warmup starts remain included
in logical-attempt accounting even when they finish inside the window.

Throughput uses the measurement start through the later of window end and last
measured completion. Missing throughput or latency is null. P50/P99 use an
Algorithm R reservoir of at most 16,384 successful reads; maximum latency and
operation totals cover all measured completions. Approximate percentiles must
be labeled as such in later reports.

An operation failure, interruption or drain timeout cancels the other workers,
then joins all of them. Operations must honor their cancellation token, as the
HTTP lanes will; an arbitrary non-cooperative callback cannot be forcibly
stopped. Controls use a manually advanced clock and synchronization signals,
with no wall-clock sleeps. This does not add a live runner or establish any
performance result.
