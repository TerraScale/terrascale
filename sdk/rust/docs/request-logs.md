`Database::list_database_logs(None)` returns the newest 100 recorded requests
within 24 hours; pass `Some("2xx")`, `"3xx"`, `"4xx"` or `"5xx"` to filter.
`databases:read` is required. Routes are server templates, not URLs or query text.
`truncated` and `recording_limited` report window/capacity omissions; recording is
best effort, not an audit guarantee. Request IDs support customer correlation.
Operation counts accept exact int64 integers/strings; latency retains decimal
text through `Number`. Query observations at least 100 ms are slow queries.
