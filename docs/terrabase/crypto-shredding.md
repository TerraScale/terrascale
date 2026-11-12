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

A node refuses a shred record for `ts-auth` (database `00000000000000000000000003` of project
`00000000000000000000000002`) or `ts-dashboard` (database `00000000000000000000000004` of project
`00000000000000000000000005`), which a deletion of that database leaves in the custody bucket `ts-terrabase-shapes-prod`
([custody](crypto-shredding-f11.md#custody-outside-backups)): it keeps the database and logs one `WARN` per process,
`Refused a shred record for platform database <database> of project <project>, deleted at <time> by another node: its
catalog record and files stay, and its backups have stopped until an operator removes the record`. The record's path
names no region, and every region keeps its platform databases under the same ids, so one record stops that database's
backups in every region. While the record exists every backup of that database fails, and `GET /v1/backup` shows
`"lastError":"DatabaseShreddedException"` for its file. TerraBase has no endpoint that reads or removes the record, so
the first two steps use `cf r2 objects` on the custody bucket.

Remove only a record for database `00000000000000000000000003` or `00000000000000000000000004` that a `Refused a shred
record` line names. Never remove the record of a customer database: it is that deletion's authority, and a node that
has not applied it yet would keep the deleted database and back it up again under a new key.

1. Check the record: `cf r2 objects get v1/keys/00000000000000000000000001/<project>/<database>/shredded --bucket-name
   ts-terrabase-shapes-prod --text` prints it, and its `databaseId` and `shreddedAt` match the `WARN` line, for example
   `{"databaseId":"00000000000000000000000003","shreddedAt":"2026-11-12T03:47:45Z"}`. The data keys beside it are gone:
   the deleting node destroyed them, and `cf r2 objects list --bucket-name ts-terrabase-shapes-prod --prefix
   v1/keys/00000000000000000000000001/<project>/<database>/` lists only `shredded`.
2. Remove the record: `cf r2 objects delete v1/keys/00000000000000000000000001/<project>/<database>/shredded
   --bucket-name ts-terrabase-shapes-prod`. Within one backup pass the node creates a new data key under the same prefix
   and ships again, and `GET /v1/backup` shows `"lastError":null`.
3. Know what the bucket can restore. Removing the record starts no new backup generation: the database's continuous
   backup goes on from its generation's base, written under the destroyed key, until the next daily snapshot, up to a
   day later, stores one under the new key. Until then a restore from the bucket needs that base and fails with `a
   backup object names a data key the bucket does not hold`. Restarting TerraBase on the node that ships the database
   ends this at once: every start begins a new generation under the new key and drills it. Times before the restart or
   the daily snapshot stay unrestorable, and the daily snapshot's own restore drill still rebuilds from the old base and
   fails the same way (`The restore drill of … failed`), also on later days while no write moves the log past the
   previous snapshot. A snapshot taken with `POST /v1/projects/<project>/databases/<database>/backups` is a separate
   backup kept as its own file, not a new base, and restores of a platform database through the API, point in time or
   from such a snapshot, answer `409 recovery_erasure_fenced`.

See [backup.md](backup.md) for backup encryption and [deletion-retention.md](deletion-retention.md) for physical erasure.
