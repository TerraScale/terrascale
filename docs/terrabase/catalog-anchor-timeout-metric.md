# Catalog anchor timeout history

`terrabase_catalog_anchor_timeouts_total` is a process-lifetime monotonic counter on
the metrics listener. It exports zero before any timeout, including on nodes without
an anchor bucket. It has no application dimensions; the exporter adds the fixed
`otel_scope_name="TerraScale.TerraBase"` label.

Each anchor attempt increments once when cancellation is caused by its own ten-second
deadline, including time waiting for the anchor writer semaphore. Caller cancellation
(including service shutdown), unrelated cancellation and storage failures do not count.
If caller cancellation and the deadline overlap, caller cancellation takes precedence.
The counter resets on process restart; use `increase` or `rate` for history. A zero
value is healthy telemetry, not a missing signal. The metric changes no storage format,
anchor admission, retry, or acknowledgement behavior.
