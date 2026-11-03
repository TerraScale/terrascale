# Tigris storage through Fly

Tigris remains the object store for TerraBase backups. Its organization and bucket
management remain connected to Fly even though TerraScale compute runs on OCI.
Do not remove or rename the storage integration during the ts-orch migration.

- `flyctl auth login` remains available for Tigris account access.
- `fly storage create` remains the bucket-creation command for this integration.
- The S3 endpoint remains `https://fly.storage.tigris.dev`.
- Existing `TIGRIS_*` configuration names remain unchanged. TerraBase also consumes
  the bucket's `AWS_*` and `BUCKET_NAME` environment variables.

Production credential values belong in OCI Vault, retrieved with scoped instance
principals. Never print bucket credentials or commit them to a repository.
Storage commands here identify retained capabilities; they are not authorization
to create or destroy buckets. Existing bucket contents and credentials must be preserved.

## Buckets

All live in the `terrascale` Fly organization:

| Bucket | Consumer | Credentials |
| --- | --- | --- |
| `terrascale-gru-terrabase-backups-b12` | Production TerraBase in every region (backups, catalog anchors) | `gru-1-terrabase-backup-access-key-id`, `gru-1-terrabase-backup-secret-key` (gru OCI Vault) |
| `ts-terrabase-ci` | TerraBase CI | `TIGRIS_CI_CREDENTIALS` (GitHub Actions), `dev-tigris-ci-env` (dev Vault) |
| `ts-terrabase-bench` | Benchmarks | `dev-tigris-bench-env` (dev Vault) |

Losing the production bucket closes every restarted TerraBase catalog. See
[the 2026-11-03 incident](qa/P0-TIGRIS.md) for recovery.
