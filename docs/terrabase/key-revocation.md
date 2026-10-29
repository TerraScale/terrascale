# API key revocation and expiry on every transport

A dashboard API key (a ts-auth client credential) that is revoked, rotated without grace, expired, or whose organization
or project was deleted stops working on every TerraBase transport in every region within **2 seconds** of ts-auth
acknowledging the change. Work already running on the key is cut off within **2.5 seconds** (the 2-second answer plus one
250 ms poll). If ts-auth cannot answer,
TerraBase refuses the key (fail closed).

This replaces the earlier bound, where a key's 60-second access tokens kept working until they expired.

## Where authority lives

ts-auth owns API keys. Its key records live in its own store behind the GRU storage authority, and only ts-auth reads them.
TerraBase never reads ts-auth storage. It asks ts-auth through its public API:

- `GET /v1/terrascale/api-keys/self` with `Authorization: Bearer <access token issued for the key>`. The token is the
  only credential needed.
- 200 `{"key_id": "..."}` while the key exists and has not expired, the key string the token was issued for still works
  (the current one, or the previous one during its rotation grace), and the organization and project still exist.
- 401 `{"error": {"code": "key_revoked" | "key_expired" | "invalid_token", ...}}` otherwise. Every call reads storage
  and nothing is cached (`no-store`).

Access tokens that ts-auth issues for a key carry `key_id` (equal to `sub`) and `key_gen` (the first 16 hex digits of the
SHA-256 of the key string they came from). ts-auth caps their expiry at the key's own `expires_at`.

## How TerraBase checks (`IKeyAuthority`, every region)

- Every HTTP request with a key's token asks `IKeyAuthority`. That covers documents, queries, transactions, HTTP SQL
  and change streams. The check runs in the project-check middleware, before scope authorization.
- On Postgres, every dashboard key is exchanged at ts-auth's token endpoint. It is checked at startup and again before every
  statement, including the rows a Describe already read, which are sent only if the key still acts at Execute.
- One answer per (key, key string, project) per node. A **yes** holds for 2 seconds from when the question was *sent*,
  so a slow answer cannot stretch the window. After 1 second it is asked again in the background, so a busy key never
  waits. A **refusal** holds for good: key ids are ULIDs and are never reused, and retired key strings and deleted
  projects do not come back. At most one question per key is in flight.
- ts-auth unreachable, slow (2-second timeout), 5xx, 429 or 404: the answer is *unavailable* and never a stale yes. HTTP
  then answers `503 auth_unavailable` with `Retry-After: 1`, and Postgres answers FATAL `57P03 auth_unavailable`.
- No events are pushed, so no new replication or catalog path is added. The 2-second check already meets the bound in
  every region. A push channel would only shorten a window that is already short. CatalogGate's anchored revocations
  of TerraBase's own `tb_` keys are unchanged.

## Open connections and long work

| Transport | New requests / statements | Already running |
|---|---|---|
| HTTP (documents, SQL) | `401 key_revoked` / `401 key_expired` (with `WWW-Authenticate: Bearer error="invalid_token"`) | the request is cut off within 2.5 s, including slow request bodies. Before its response starts it is answered like a new request: the `401` above, or `503 auth_unavailable` with `Retry-After: 1` when the key could not be verified in time. A response already under way is aborted |
| Change streams (SSE) | refused as above | the connection is aborted within 2.5 s |
| Postgres | FATAL `28000 key_revoked: ...` / `key_expired: ...`, then the connection closes | the session is watched for its whole life, idle or busy. Its TCP connection is aborted within 2.5 s and unflushed rows are dropped. An open transaction rolls back |

A request or session is watched by asking `IKeyAuthority` every 250 ms while it runs. These checks hit the same
per-node answers, so a busy key still costs about one ts-auth call per node per second. Running work never outlives the
last yes. If no new yes arrives before that one runs out (2 s after it was asked), the work is cut off, and waiting for
ts-auth does not extend it. A slow ts-auth therefore ends long requests with `503 auth_unavailable` rather than a reset. A Postgres session exchanges its key again as each 60-second token lapses, as statements
do, so an idle session that is still valid stays open. Clients should treat `key_revoked` and
`key_expired` as permanent: stop retrying and ask for a new key. Treat `auth_unavailable` as retryable.

## Mixed versions

Deploy order does not matter. TerraBase works with a ts-auth from before key status, in either order:

- A token without `key_id`/`key_gen` is checked as a token only (signature, issuer, audience, expiry, claims), as it was
  before key status, on HTTP and on Postgres. The 60-second token lifetime bounds it.
- When `/api-keys/self` answers 404, ts-auth predates key status, and the token's own checks stand.
- A token carrying the claims must carry both, with `key_id` equal to `sub` and a well-formed `key_gen`. Otherwise it is
  refused.

The bounds above apply once ts-auth issues the claims and serves `/api-keys/self` everywhere, and its previous tokens
(60 s) have expired.

## Service keys and sign-in

A key does not depend on the sign-in of the user who created it. The check reads only the key, its organization and its
project, so signing out, resetting a password, or removing the creator's session never disables a key.

## Cost and limits

ts-auth answers the status check only on the GRU authority, where revocations are written. Any other region answers
503, so a lagging regional copy can never say yes. It admits at most 512 checks in flight per instance, and at most 20
storage-backed checks per second for one key, key string and project. Beyond those limits it answers 503 or 429, and
TerraBase refuses on both. A caller hammering its own key throttles only that key.

A node asks about each active key about once a second. From `sjc` and `fra`, that is one round trip to ts-auth in `gru`.
Requests never wait on it while the key stays busy. An idle key's first request waits for one round trip.

## Not covered

- Signed file download URLs and shape links that were already handed out keep working until their own short expiry.
