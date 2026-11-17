# Customer JavaScript functions

This source candidate adds all six function routes through database-scoped
handles. Root wrappers await the concurrent safety changes; live qualification
and publication remain separate work.

```python
from terrascale.functions import DatabaseFunctions, FunctionDeployment

functions = DatabaseFunctions(client.database("main"))
deployed = functions.deploy_function("hello", FunctionDeployment(
    source="function handler(event) { return event.body; }",
    cron="0 * * * *",
))
listed = functions.list_functions()  # source is None in list results
loaded = functions.get_function("hello")
result = functions.invoke_function("hello", {"name": "Ada"})
logs = functions.list_function_logs("hello", limit=10)
stats = functions.get_function_stats("hello", window="1h")
functions.delete_function("hello")
```

Use `AsyncDatabaseFunctions(async_client.database("main"))` and await each
method for the same typed DTOs. Invocation accepts JSON, including `None`, and
optional query values. Results can be any JSON value, a text response, or `None`
for an empty response. Source is limited to 256 KiB and invocation JSON to 1 MiB.
Cron expressions are five-field UTC; JavaScript and cron validation run on the
server. Deploy/delete/invoke are never automatically replayed after a transport failure.
Reconcile an uncertain result before retrying. Logs are newest first, limited
to 1–100 per request.

## Stats

`get_function_stats` reads a function's aggregated invocation metrics. The
window is `1h`, `24h` (the default when omitted) or `7d`:

```python
stats = functions.get_function_stats("hello", window="24h")
stats.invocations, stats.errors, stats.p50_ms, stats.p95_ms, stats.p99_ms, stats.max_ms
stats.sql_calls, stats.rows_read, stats.rows_written
stats.buckets[0].at, stats.buckets[0].invocations
```

Buckets are UTC and oldest first, including empty intervals: 60 minute buckets
for `1h` and 24 or 168 hour buckets for `24h` and `7d`. Metrics survive the
last-100 log limit and need the same read and write scopes as logs.
