# Request-log status filter

`GET /v1/projects/{project}/databases/{database}/logs` accepts one optional
`status` query parameter: `2xx`, `3xx`, `4xx`, or `5xx`. Omission reads all
status classes. Invalid or repeated values return `400 invalid_status`.
The public generated OpenAPI operation `listDatabaseLogs` describes this
existing filter with the same enum and no default value.

The endpoint still requires project authority with `databases:read` and returns
at most the latest 100 recorded requests within 24 hours. Recording is best
effort. This metadata correction adds no durable export, cursor, retention,
pagination, retry, or log-drain guarantee.
