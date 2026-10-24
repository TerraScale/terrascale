# Project metrics export and dashboard reads

## `GET /v1/projects/{project}/metrics`

A public Prometheus scrape target. It uses the same authorization as `GET /v1/projects/{project}/analytics`:
a ts-auth project access token with `databases:read`. A token restricted to one database exports only that
database; a token for another project receives 404 and a request without a token 401. Customers
configure Prometheus `oauth2` with `client_credentials` against
`https://auth.terrascale.tech/terrascale/oauth/token`, using an API key that holds `databases:read`.

The response is `text/plain; version=0.0.4; charset=utf-8` with `Cache-Control: no-store`. It reads the
`range=1h` analytics (1-minute UTC buckets) and ignores the current, partial minute. Every series is a gauge
labelled `database` (name), `database_id` (catalog ULID) and `region` (observing region):

| Metric | Value |
|---|---|
| `terrascale_database_requests_per_second` | Client requests of every method in the last 5 complete minutes, divided by 300 |
| `terrascale_database_errors_per_second` | Error responses in the same 5 minutes, divided by 300 |
| `terrascale_database_operations_per_second{kind}` | Committed document operations per kind in the same 5 minutes, divided by 300 |
| `terrascale_database_latency_milliseconds{quantile="0.5"\|"0.95"\|"0.99"}` | Percentiles of the newest complete minute with requests; omitted without one |
| `terrascale_database_storage_bytes` | Newest sampled on-disk bytes of the last hour; omitted before sampling |

Values are best-effort observations, flushed every 10 seconds, not billing. Label values are escaped as the
exposition format requires. These labels name the caller's own databases in a per-project response; they are
never recorded on the `TerraScale.TerraBase` operator meter, so the bounded-label rule for the administrative
`/metrics` scrape is unaffected.

## Internal dashboard reads

Served only on the internal listener (shared peer credential), like `GET /v1/usage/allowances`. TerraBase
resolves the project's organization from its catalog; callers never supply one. A malformed id answers 400 and
an unknown project or database 404. Responses are never cached.

- `GET /v1/usage/projects/{project}/analytics?range=1h|24h|7d|30d&database=<catalog id>&region=<region>`:
  exactly the `ProjectAnalytics` JSON of `GET /v1/projects/{project}/analytics`, for every database of the project.
- `GET /v1/usage/projects/{project}/databases/{databaseId}/logs`: exactly the `DatabaseRequestLogs` JSON of
  `GET /v1/projects/{project}/databases/{database}/logs` without a status filter (newest 100 within 24 hours).
  `databaseId` is the catalog ULID.
