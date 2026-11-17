# Client-aborted requests

When an HTTP request throws `OperationCanceledException` and its `RequestAborted` token is canceled, TerraBase records status 499 (client closed request). The request remains in request counts and latency observations, and appears under the 4xx request-log filter. It contributes no request errors to usage or live database statistics and never appears under the 5xx filter.

Cancellation without a canceled request token remains a server error. Function execution propagates caller cancellation to request metering instead of creating a failed function result or invocation error record. Function execution deadlines still produce `500 function_error`.

Dashboard request timing logs use 499 for the same client-abort condition. Dashboard service telemetry excludes those abandoned requests even when a dependency wraps cancellation in an exception carrying a different token. Other failures and server-side cancellation remain failures.

The journey invokes a real function that writes a marker before awaiting a pending promise, waits for the marker, and aborts the caller. It checks the exact request's 499 log entry and absence from the 5xx filter, and confirms no function error log or invocation errors were recorded. The existing database-tab and mobile-page assertions continue checking their server-error views.
