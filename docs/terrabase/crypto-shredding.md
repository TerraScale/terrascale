# Crypto-shredding of deleted databases

A successful database DELETE destroys its wrapped envelope keys before returning 204. If custody cannot confirm
absence, DELETE returns 503 and durable erasure work retries. Project and account deletion apply the same authority
to every database they own; a project closure answers `accepted` only once those keys are destroyed, by its own
request when they fit in its 10-second erasure budget, otherwise by the purge passes or a later request.

The [F11 deletion contract](crypto-shredding-f11.md) specifies key custody, persisted-copy protection, regional
convergence, CDN purge retries and the format reset. Wrapped keys live in the existing R2 custody prefix outside
Tigris and customer backup destinations. They are never copied into a bucket snapshot.

TBK1 backups, TBS1 shapes and TBP1 durable push/partner copies require those per-database keys. Historical decrypt
checks the shared deletion record before using a cached key. Regional sweeps close database files and remove local
keys; erasure passes remove backup and shape objects. A durable outbox retains CDN purge work until Cloudflare
acknowledges it.

Deleted databases normally return 404 through public routes. Internal restore paths that retain the database identity
return `409 database_shredded`. Shape key requests release no key after deletion. Already delivered plaintext or
client-held shape epoch keys cannot be revoked.

## A shred record for a platform database

A node refuses a shred record for `ts-auth` or `ts-dashboard`, which a deletion of that database leaves in the shape
storage bucket: it keeps the database and logs one `WARN` per process, `Refused a shred record for platform database
<database> of project <project>, deleted at <time> by another node: its catalog record and files stay, and its backups
have stopped until an operator removes the record`. While the record exists every backup of that database fails, and
`GET /v1/backup` shows `"lastError":"DatabaseShreddedException"` for its file. TerraBase has no endpoint that reads or
removes the record, so the first two steps work on the shape storage bucket directly.

1. Check the record: read `v1/keys/<organization>/<project>/<database>/shredded` and compare its `databaseId` and
   `shreddedAt` with the `WARN` line, for example `{"databaseId":"00000000000000000000000003","shreddedAt":"2026-11-12T03:47:45Z"}`.
   The data keys beside it are gone: the deleting node destroyed them.
2. Remove the record: delete that object. Within one backup pass the node creates a new data key under the same prefix
   and ships again, and `GET /v1/backup` shows `"lastError":null`.
3. Take a new snapshot: `POST /v1/projects/<project>/databases/<database>/backups` answers 201 with `"status":"ready"`.
   What was shipped before the deletion used the destroyed key; this snapshot uses the new one.

See [backup.md](backup.md) for backup encryption and [deletion-retention.md](deletion-retention.md) for physical erasure.
