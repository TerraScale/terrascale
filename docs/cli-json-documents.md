# Collection, document and SQL JSON

Collection lists return arrays; create/drop return SDK collection metadata.
Document get returns the document envelope; put/delete return the write result,
including its version and durability metadata. Client document import returns
`{imported:<count>}` only after every input line succeeds. Failed imports may have
already written earlier lines: a nonzero exit does not mean rollback.

`doc list --json` uses the common record/complete/error NDJSON contract; ordinary
document listing remains import-compatible. `sql --json` returns `{matched,truncated,rows}`
so truncation is explicit. `exec --json` returns columns, positional rows, affected
count and last insert ID from the SDK, preserving duplicate column names and integers.
These bounded SQL responses are single JSON objects, not NDJSON streams.
