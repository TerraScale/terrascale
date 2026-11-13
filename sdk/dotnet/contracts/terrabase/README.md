# TerraBase SDK contract

- `routes.json` records every client method and path.
- `database-deletion.openapi.json` is the scoped live OpenAPI export for seven-day deletion and restoration, excluding common response headers and examples. Regenerate with `python3 scripts/terrabase/export-deletion-openapi.py <openapi-v1.json> sdk/dotnet/contracts/terrabase/database-deletion.openapi.json` from the repo root. SDK convenience methods ship after this producer is deployed.
- `current-public-routes.json` is the full public route inventory of TerraBase's `/openapi/v1.json` at its `serverCommit`.
- `sql/` holds the SQL error codes and WebSocket session contract shared by every SDK; see [`sdk/SQL-API.md`](../../../SQL-API.md), [`sql-execute.md`](sql-execute.md) and [`sql-websocket.md`](sql-websocket.md).
- `functions.json`, `function-invocations.json`, `file-storage/`, `project-metrics.json`, `public-health.md`, `regions.md`, `request-logs.md`, `database-rename.md` and `project-erasure.md` pin their scoped producer sources.

## Route, identity and scope

Project lifecycle, usage and branch methods use `/v1/projects/{project}`. `{project}` is the canonical ts-auth project ULID. `{database}` is its name, or its immutable ULID where the route says so. A database lives in exactly one region, `homeRegion`; `region` names the region that answered. List pagination is `items`/nullable `next`, query `after` and `limit` 1–100 (default 20).

Lifecycle reads and usage require `databases:read`; create, delete, rename and branch require `databases:write`. Project/database mismatch is 404, not disclosure of foreign ownership. All mutations send once; there are no transport retries.

## Usage

Usage JSON carries 64-bit totals, a `period` (`from`, `to`, `granularity`, `reads`, `writes`), oldest-first `series` (`start`, `reads`, `writes`), and per-database `items` on project responses. `granularity` accepts hour or day. `from` is inclusive and rounded down, `to` exclusive and rounded up. A project over 100 databases or an expensive query fails 400 rather than reporting zero usage. These counters are best-effort dashboard usage, not billing data.

## Routing errors

`TerraBaseWrongRegionException` preserves 421 and exposes the validated answering `Region`, `HomeRegion`, `Regions` and `ErrorCode`. The SDK never redirects, retries or changes hosts on 421.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
