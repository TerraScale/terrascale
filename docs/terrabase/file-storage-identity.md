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
`terrascale-gru-files`, `terrascale-sjc-files` and `terrascale-fra-files`,
and it lists objects in those three buckets: a project closure is sent to
every serving region, and each region's node then lists the closed project's
prefix, `app-files/<organization>/<project>/`, in each of the three and deletes
what it finds ([account-project-erasure.md](account-project-erasure.md)). It needs no
bucket-level permission beyond that list.

Grant list on the three buckets before the credentials are installed on any
node, and create a region's files bucket before the region is in the peers
file. Without the list permission, or without the bucket, every sweep of that
region is refused: it is counted in `terrabase_file_storage_cleanup_errors_total`
(`pass="closed_projects"`, with the region), logged and retried, and the sweep
marker of each project closed in that time is kept past its end until one sweep
lists every region. Nothing else breaks: uploads, downloads, deletes and the
other regions' sweeps go on, and the node keeps running.
Signing uses region `auto`; bucket names come from the serving region. Tigris
is accepted only in the Development environment for tests.

Absent, partial, unreadable or malformed file credentials leave record and
backup service available. File transfers return 503 and release their storage
reservation. A node without credentials makes no list call: it keeps the sweep
marker of each closed project until the marker's end, 24 hours and 5 minutes
after it was written, sweeps it if it runs with credentials before then, and
removes it after that. Such a node still takes a project closure from ts-auth
or from another region: it fences the project, erases its bucket definitions
and acknowledges, because those records need no credentials and a node that
never had credentials holds no file record. A node that holds file records and
lost its credentials cannot delete their objects: its region keeps the closure
pending, and the region that asked keeps sending it, until the credentials
are back. During a rolling deploy a node of an earlier release
answers a closure sent by another region for itself, as the newer nodes do.
Supplying Tigris backup credentials alone never enables transfers.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
