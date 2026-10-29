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
Backup/PITR and project data-key custody keep their existing Tigris identity.
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
pivot deploy lane owns the format bump and rebootstrap. F11 still owns
independently erasable keys; purge acceptance is not cryptographic erasure.
No visual change.
