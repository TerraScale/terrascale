# Customer-file identity

Customer files use an R2 identity independent of continuous backups. Backups
keep their Tigris endpoint, bucket and credentials unchanged. File storage never
falls back to `BackupSettings` or any `AWS_*` backup variable.

OCI Vault delivery supplies these references (the application uses its existing
`TERRABASE_` environment prefix):

- `TERRABASE_FILES_S3_ACCESS_KEY_ID_FILE`: `gru-1-terrabase-files-access-key-id`
- `TERRABASE_FILES_S3_SECRET_ACCESS_KEY_FILE`: `gru-1-terrabase-files-secret-key`
- `TERRABASE_FILES_S3_ENDPOINT`: `https://<account-id>.r2.cloudflarestorage.com`

The identity needs object read/write/delete access only to
`terrascale-gru-files`, `terrascale-sjc-files` and `terrascale-fra-files`.
Signing uses region `auto`; bucket names come from the serving region. Tigris
is accepted only in the Development environment for tests.

Absent, partial, unreadable or malformed file credentials leave document and
backup service available. File transfers return 503 and release their storage
reservation. Supplying Tigris backup credentials alone never enables transfers.
