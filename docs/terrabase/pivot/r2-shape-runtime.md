# F6 shape storage runtime identity

Production shapes use the shared `ts-terrabase-shapes-prod` R2 bucket in
account `6cd3f3639e928077593d9b470765efeb`, signing region `auto`, and
`https://shapes-ts-terrabase-shapes-prod.terrascale.tech/`. These identities
are code constants for gru, sjc and fra; there are no endpoint or bucket knobs.

| OCI Vault secret | ts-orch file binding |
| --- | --- |
| `gru-1-terrabase-shapes-access-key-id` | `TERRABASE_SHAPES_ACCESS_KEY_ID_FILE` |
| `gru-1-terrabase-shapes-secret-access-key` | `TERRABASE_SHAPES_SECRET_ACCESS_KEY_FILE` |
| `gru-1-terrabase-shapes-cdn-api-token` | `TERRABASE_SHAPES_CDN_API_TOKEN_FILE` |

The existing secret-file loader reads those mounts before configuration.
Unreadable files, incomplete identities and malformed credential values exit
with code 2. Outside Development an absent identity also exits with code 2,
before host construction. Diagnostics name settings and never their values.
Runtime storage failures leave an object unpublished; there is no backup-key
or presigned-link fallback in production. Development integration fixtures
inject their own isolated shape identity and named HTTP transport.

PUT, HEAD, LIST and DELETE all use `shapes-s3` and the shape credentials.
Backup/PITR ciphertext keeps its existing Tigris identity. F11 moves database envelope-key custody to the
private R2 `v1/keys/` prefix outside every backup destination; see [the F11 contract](../crypto-shredding-f11.md).
CDN purge and analytics use only the separate shape CDN token. Purges accept
only canonical production object URLs and submit handle prefixes, clearing
all browser Origin variants. Each worker retains its 4,096-entry bound,
coalesces a batch of at most 100 prefixes, and spaces API calls by 12 seconds.
This is a per-worker limit, not the shared account-wide rate budget.

This change is the storage-identity part of F6. The existing volatile purge
queue is not durable. The catalog inventory/outbox, leased cleanup, parked
failures, admission and account-wide budget in
[r2-cdn-production.md](r2-cdn-production.md) remain required before F6 cleanup
qualification; restart or queue saturation can still lose purge work.
No storage-format change or historical-object migration occurs here. The
format bump and rebootstrap need the user's approval. F11 still owns
independently erasable keys; purge acceptance is not cryptographic erasure.
No visual change.

## Live state and proof (2026-11-04)

Provisioned and verified read-only on 2026-11-04 with `cf`:

| Resource | State |
| --- | --- |
| Bucket `ts-terrabase-shapes-prod` | ENAM, Standard, default jurisdiction, created 2026-10-29 |
| Custom domain `shapes-ts-terrabase-shapes-prod.terrascale.tech` | ownership and SSL active, minimum TLS 1.2 |
| Lifecycle | `shapes-expire-8d` on `v1/shapes/` (691200 s); `abort-multipart-1d` |
| CORS | `shapes-browser-get`: origins `*`, GET/HEAD, no credentials |
| Zone WAF `e63dd3e752a547f48bbb0ee49b76767c` | blocks non-GET/HEAD, query strings, `..`, raw `%` and non-`/v1/shapes/` paths on `shapes-*` |
| Zone cache rule `0ab9b726a5254876a3fde8a06f2f9bf3` | `shapes-*.terrascale.tech/v1/shapes/`, empty query, GET/HEAD/PURGE; respect origin TTLs; 400–599 no-store |
| Smart Tiered Cache | on |
| Runtime identity | ts-orch binds the three `gru-1-terrabase-shapes-*` Vault secrets (`deploy/m1/services/terrabase.json`); gru TerraBase starts only with them |

Live gru proof with a throwaway Free account (deleted afterwards): a SQL table shape's snapshot page `snap.7.0`
and change chunk `7` were listed by the manifest at the production CDN URL. Each object answered `MISS` once and then
`HIT` from CWB, with `Cache-Control: public, max-age=31536000, immutable`, a `TBS1` body and the manifest's SHA-256.
The released epoch key opened the cached chunk. POST, query strings and non-shape paths answered 403. Twenty origin
shape and manifest requests left the database's usage at `reads: 0` (shape reads are never metered, P6).

After `DELETE …/databases/{database}` (204) the CDN still served the same ciphertext, and no key was released. Shape
epoch keys derive from the database's own data key (`ShapeCrypto.Scope` is `BackupLayout.KeyScope`), so the
[crypto-shredding](../crypto-shredding.md) of that key also covers every shape object of the database and its tenant.

The `ts-terrabase-shapes-test` bucket and the `R2_SHAPES_CI_CREDENTIALS` secret are unused.

Still open: the durable purge outbox and account-wide purge budget above; deleting a database does not yet remove
its R2 objects or queue their prefix purge (the eight-day lifecycle removes them, and crypto-shredding makes cached
copies unreadable first). Observed live: about ten minutes after sealing, `manifest?offset=7` listed nothing until an origin read of offset 7
re-adopted the object; the snapshot page stayed listed.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
