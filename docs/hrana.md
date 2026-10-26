`Database::libsql_pipeline(HranaVersion::V3, &envelope)` supports v2/v3 Hrana
HTTP JSON. Supply exact protocol snake_case fields, typed values, and the opaque
baton returned by the previous response. SQL failures remain in-band results;
request refusals retain status and known protocol codes, including STREAM_EXPIRED.
`libsql_cursor(&envelope)` reads a complete v3 NDJSON cursor within the existing
response-size bound. Neither API follows base_url or retries uncertain writes.
Batons expire after 30 idle seconds and may bind transactions/stored SQL: never
restart a lost stream automatically. Debug/errors redact batons. Both APIs
require documents:read, plus documents:write for writes; requests are at most 1 MiB.
