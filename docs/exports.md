# Streaming exports and customer-bucket schedules

```python
from contextlib import closing
from terrascale.exports import DatabaseExports, IncompleteExportError

exports = DatabaseExports(client.database("main"))
try:
    with closing(exports.iter_export()) as stream:
        for record in stream:
            consume(record.collection, record.id, record.document)
except IncompleteExportError as error:
    # Persist error.after_collection and error.after_id with the delivered prefix.
    # Resume explicitly with both values; the resumed part uses a new snapshot.
    raise
schedule = exports.get_export_schedule()
schedule = exports.set_export_schedule("0 3 * * *")
```

For asyncio, use `AsyncDatabaseExports(async_database)`, await schedule methods,
and use `async for` with `contextlib.aclosing(exports.iter_export())`. Closing a
partially consumed iterator releases the HTTP response. Data is streamed, with
a fixed 4 MiB record bound. Records are NDJSON in collection/ID order. Each
uninterrupted request reads one snapshot; transport/framing errors, malformed
records and an unterminated final line raise `IncompleteExportError`. Streams
are never restarted automatically. Resume arguments are `after_collection` and
`after_id`; supply both from the last successfully emitted record.

Schedules use five-field UTC cron and require the project's customer backup
destination (409 otherwise). The existing API accepts null/empty cron to stop
a customer's scheduled exports; no SDK feature switch is introduced. Last-run
metadata preserves success/failure and exact integer counters. PUT outcomes
can be uncertain: read the schedule before retrying. Authenticated requests
refresh an expired first-party token once before streaming; redirects are not
followed.
