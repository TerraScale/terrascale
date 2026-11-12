# Changelog

## Unreleased — SQL only

`@terrascale/sdk/prisma` maps refused SQL statements to Prisma's driver-adapter
error kinds by SQLSTATE: unique, not-null and foreign-key violations, missing
tables and missing columns surface as P2002/P2011/P2003/P2021/P2022; other
refusals keep the server's message. Both the HTTPS query path and interactive
transactions share the mapping.

`@terrascale/sdk/drizzle` exports `drizzleProxy(database)`, the two callbacks
Drizzle's `sqlite-proxy` driver expects, mapped from a root database handle's
`execute` and atomic `batch`. The SDK adds no `drizzle-orm` dependency.

A client built with neither `baseUrl` nor `endpointResolver` sends to
`https://api.terrascale.tech`, like the other SDKs; passing both is still
refused.

An `apiKey` is exchanged at `https://auth.terrascale.tech` only when the client
sends to `https://api.terrascale.tech`. With any other `baseUrl`, or with an
`endpointResolver`, the client throws a `TypeError` before any request unless
the new `authBaseUrl` option names the bare ts-auth origin that exchanges the
key; `createScopedKey` uses the same origin. `authBaseUrl` without `apiKey` is
refused. Earlier versions sent the key to `https://auth.terrascale.tech` for
every TerraBase origin.

`baseUrl`, `authBaseUrl` and the identity clients' `apiBaseUrl` follow the origin
rule every SDK shares (`sdk/SQL-API.md`): https, or http only for `localhost`,
`127.0.0.0/8` and `[::1]` (no longer `*.localhost`, or `*.test` next to a custom
`fetch`); no user info, query, fragment or path, where a `baseUrl` path prefix
was kept before; and a `terrascale.tech` host only for its own service
(`api.terrascale.tech` and `<cell>.api.terrascale.tech` for TerraBase,
`auth.terrascale.tech` and `<cell>.auth.terrascale.tech` for ts-auth), so
`authBaseUrl` refuses `api.terrascale.tech` and its regional hosts. The string is
checked as written, before the URL parser can repair it: whitespace, `%`, `\`, a
non-ASCII character, a trailing dot, an empty user info, an IPv4 address in any
form but four decimal octets and a port with a leading zero are refused, where
earlier versions sent to the repaired host. So are a string of more than 2,048
characters, a name of more than 253 characters or with a label of more than 63,
a label that begins or ends with a hyphen or begins with `xn--` (internationalised
names), the name `loopback` and the unspecified addresses `0.0.0.0`, `[::]` and
`[::ffff:0:0]`. The client throws a `TypeError` when it is built.

A key exchange or `createScopedKey` call that fails before ts-auth answers
throws `RequestTransportError`, with the runtime's failure as its `cause`, as
data calls do; earlier versions threw the runtime's bare `TypeError`.

`rotateOrganizationApiKey` and `rotateProjectApiKey` of
`createTerraScaleIdentityManagementClient` take the `rotate_api_key` step-up
proof as `options.stepUpProof` and send it as `X-TerraScale-Step-Up-Proof`,
which ts-auth requires; without it they throw a `TypeError` before any request.
Earlier versions refused the option, so every rotation failed with
`step_up_required`. `TsAuthClient.apiKeys` (`TsAuthApiKeysClient`) is gone: it
called `/v1/{slug}/api-keys`, which ts-auth does not serve.

`execute` on a database, on a SQL session and inside `transaction` also takes
one `{ sql, args }` or `{ sql, named }` statement value. Arguments next to a
statement value, or any other value, are refused with a `TypeError` before
anything is sent.

`createTerraBaseClientFromEnvironment` sends to `TERRASCALE_BASE_URL` when
neither `baseUrl` nor `endpointResolver` is passed, and to
`https://api.terrascale.tech` when that variable is unset too; passing both
`baseUrl` and `endpointResolver` is refused.

`deleteOrganization` and `startOwnershipTransfer` now require
`options.stepUpProof` and send it as `X-TerraScale-Step-Up-Proof`, matching the
purpose-bound, organization-bound proofs ts-auth requires since #1341. The proof
is single-use and these mutations are never retried. `deleteOrganization` first
consumes the proof at `POST /v1/terrascale/step-up/consume` for its organization
and sends the `DELETE` with the same proof only after that answers 204; a refused
consume returns its error and sends no `DELETE`. Pass the approved proof
unconsumed. Published versions before this release cannot delete an
organization: ts-auth refuses an organization `DELETE` whose proof was not
consumed first.

TerraScale is SQL-only. The JSON data API is removed without a replacement shim,
with its handles, `db.transaction()`, filters and queries, shapes and replicas,
`db.changes()`, `db.export()`, imports, indexes, inferred and declared schemas,
TTL, change notifications, the read-only
`db.sql/checkSql/explainSql` subset, platform data reads, the `./codegen`
entrypoint and the `terrascale-models` CLI. Database placement is one region:
`replicaRegions`, `allRegions`, `databases.update` and the replication reads are
removed. SQL over HTTPS and WebSocket, live SQL table shapes, the database
catalog, backups, usage and file buckets remain.

The new `@terrascale/sdk/prisma` export exposes `PrismaTerraScale`, a driver
adapter factory over a `V1Database` for the ORM's driver-adapters interface:
`queryRaw`, `executeRaw`, `executeScript`, typed column mapping and the last
insert id, with queries running over HTTPS. `startTransaction` runs interactive
transactions over the SQL WebSocket session: `begin`, per-statement
`queryRaw`/`executeRaw`, savepoints, and `commit`/`rollback`, each transaction
limited to 5 seconds by the server; only `SERIALIZABLE` isolation is accepted.

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
