# Function metrics

The Functions detail page reads aggregate metrics through `IFunctionsService.StatsAsync` using the same project authority as invocation logs: SQL read and write access is required. It defaults to `window=24h`; `1h` and `7d` select the other windows.

Invocations and SQL calls come from the aggregate contract, independently of the latest 25 retained invocation records displayed by the dashboard. Error rate is errors divided by invocations, displayed as a percentage or an em dash when no calls exist. p50, p95 and p99 latency are the service's histogram estimates in milliseconds.

The two charts plot every UTC bucket, oldest first, including zero buckets. The volume chart shows invocations and errors; the latency chart shows p95 in milliseconds. Pointer and keyboard chart inspection use the shared analytics component.

Invocation filters preserve the selected window. `status=ok` selects succeeded calls and `status=error` selects failed calls; omitting it shows all statuses. The trigger filter remains available. Records outside the first aggregate bucket are excluded. Each native disclosure exposes console lines and the invocation error; its summary shows HTTP status, duration, SQL calls and time, rows read and written, and response bytes. A missing HTTP status is displayed as an em dash.

If the aggregate read fails, the shared optional-panel boundary logs the failure and the page displays “Function metrics are unavailable.” The function source, Test action and invocation logs remain usable.
