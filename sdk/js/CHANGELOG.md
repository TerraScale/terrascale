# Changelog

## Unreleased — SQL only

TerraScale is SQL-only. The JSON data API is removed without a replacement shim,
with its handles, `db.transaction()`, filters and queries, shapes and replicas,
`db.changes()`, `db.export()`, imports, indexes, inferred and declared schemas,
TTL, change notifications, the read-only
`db.sql/checkSql/explainSql` subset, platform data reads, the `./codegen`
entrypoint and the `terrascale-models` CLI. Database placement is one region:
`replicaRegions`, `allRegions`, `databases.update` and the replication reads are
removed. SQL over HTTPS and WebSocket, live SQL table shapes, the database
catalog, backups, usage, saved queries and file buckets remain.

## 0.0.19 — public-client refresh

`oidc.refreshPublic(clientId, refreshToken)` refreshes a public-client session
(the CLI device client) sending `grant_type=refresh_token` with `client_id` and
no client secret, matching the Go SDK's `Device.Refresh`. ts-auth rotates the
refresh token on every use; persist the returned `refresh_token`. Confidential
clients keep `oidc.token` with `client_secret_post`.

## 0.0.17 — read deadline and transient retries

Replay-safe v1 reads (GETs) now have one fixed
60-second deadline, including retries and the body, and reject with a
`TimeoutError` instead of hanging. They retry up to three times after a
connection failure, the producer's transient 429/503 refusals (for example
`429 tenant_busy` from the per-plan concurrency budget) or a body-less gateway
502/503/504, waiting at least `Retry-After` plus full-jitter backoff. The token
provider is asked again for each attempt. Mutations remain single-attempt.
`401 key_revoked` and `key_expired` throw `V1KeyRevokedError` and
`V1KeyExpiredError`.

## 0.0.15 — hosted request cancellation

Hosted identity and OIDC methods accept an optional final request-options argument
with `signal`. Existing list options retain their signal field. Cancellation stays
a transport error value, with `request_aborted` for caller cancellation and
`request_timeout` for the SDK deadline; ordinary network failures retain
`transport_error`. Cancellation details preserve a sanitized cause.

## 0.0.14 — remove the planned Better Auth stub

The package no longer exports `./better-auth` or ships its always-throwing stub.
The server-only database adapter is planned after the replication and public auth
primitive work; it is not an available SDK capability. Management and administration
operations remain available through their named entrypoints.

## Earlier releases

Releases 0.0.1 to 0.0.13 and 0.0.16 shipped the retired JSON data API and runtimes; see the git history.
