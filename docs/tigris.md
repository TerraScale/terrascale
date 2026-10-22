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
