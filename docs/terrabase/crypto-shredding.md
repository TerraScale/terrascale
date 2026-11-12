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

The shared shred guard refuses `ts-auth` (database `00000000000000000000000003` of project
`00000000000000000000000002`) and `ts-dashboard` (database `00000000000000000000000004` of project
`00000000000000000000000005`). The purge writer checks it before writing a deletion marker, invalidating cached keys,
or deleting wrapped keys. Peer revocation and the custody sweep check the same guard before invalidating keys or
removing local files. A refused shred leaves the live database and its continuous backup readable. Permitted shreds
still write the durable marker before destroying any wrapped key. Seeing a permitted marker drops peer key caches before
catalog observation or local erasure, even when either cannot complete. A purge job refused by the guard records
`shred_refused` as a terminal outcome and leaves the pending-erasure gauges.

An already existing marker for a protected database is terminal for the sweep. It logs one warning per protected
database per process, including organization, project and database ids, and increments `terrabase.keys.sweep_refused`.
Subsequent full scans skip processing that database without reading its marker or revoking its keys. After a process
restart the sweep reports the marker once again. The marker remains in custody until an operator clears it; the sweep
does not remove deletion authority. While it remains, backup writers refuse to ship that database.

The warning is `Refused a shred record for organization <organization>, project <project>, database <database>: custody
and local files stay untouched; an operator must inspect and clear the marker`. Every region shares the same custody
path, so a marker blocks backups in every region. `GET /v1/backup` reports
`"lastError":"DatabaseShreddedException"` after a writer encounters it.

### Inspecting and clearing a refused marker

TerraBase has no endpoint that reads or removes deletion records. Use `cf r2 objects` on the custody bucket
`ts-terrabase-shapes-prod` ([custody](crypto-shredding-f11.md#custody-outside-backups)). Remove only a marker for database
`00000000000000000000000003` or `00000000000000000000000004` that a refusal warning identifies. Never remove a customer
database marker: it is that deletion's authority, and a node that has not applied it yet could otherwise keep the
deleted database and back it up under a new key.

1. Inspect the marker: `cf r2 objects get v1/keys/00000000000000000000000001/<project>/<database>/shredded --bucket-name
   ts-terrabase-shapes-prod --text`. Verify the path against the organization, project and database in the warning and
   verify the JSON `databaseId`. Record its `shreddedAt`, for example
   `{"databaseId":"00000000000000000000000003","shreddedAt":"2026-11-12T03:47:45Z"}`. Inspect the wrapped-key inventory
   with `cf r2 objects list --bucket-name ts-terrabase-shapes-prod --prefix
   v1/keys/00000000000000000000000001/<project>/<database>/`. A previous faulty shred may have left only `shredded`.
2. Clear only that protected database's marker: `cf r2 objects delete
   v1/keys/00000000000000000000000001/<project>/<database>/shredded --bucket-name ts-terrabase-shapes-prod`.
3. A shipping or sealing pass that encounters the marker abandons its current generation and pending work, increments
   `terrabase.backup.abandoned` with reason `shredded`, and invalidates its key selection. After marker removal, the next
   eligible pass selects fresh keys and writes a new base snapshot and generation proof. No restart or daily snapshot
   is needed. Check `GET /v1/backup` for a new generation and `"lastError":null`, and verify a new wrapped key appears
   under the custody prefix. A process restart also begins a fresh generation.
4. If the marker was removed before the writer encountered it, sealing first resolves the active generation's base key
   through the existing key cache. Healthy cache hits add no custody requests or key-read-slot usage. If custody reports
   that the key is missing, the writer abandons the generation with reason `missing_key`, invalidates its selection and
   starts a fresh generation on the next eligible pass. Key selection still checks deletion authority, so a marker
   fences recovery. A generation whose base key is still held can continue.

### Earlier refused shreds

In the November 12 incident, custody wrote the marker and destroyed the wrapped keys before the receiving node
refused the platform database's shred. Removing that marker previously resumed the existing generation with a new key,
but its base snapshot still needed a destroyed key. Restores failed with `a backup object names a data key the bucket
does not hold` until a new decryptable base was written, normally by a daily snapshot or a process restart. The writer
now abandons the generation when shipping encounters the marker or sealing discovers the missing base key, then creates
a fresh generation on its next eligible pass after marker removal.

Destroyed-key history stays unreadable permanently. The fresh generation captures live data under newly generated
keys; it never recovers or re-wraps a destroyed key. A snapshot requested with
`POST /v1/projects/<project>/databases/<database>/backups` remains a separate backup file. API restores of a platform
database, including point-in-time and separate-snapshot restores, still answer `409 recovery_erasure_fenced`.

The restore drill selecting an older snapshot whose key was destroyed is separate work; this change does not alter
snapshot selection for historical drills.

See [backup.md](backup.md) for backup encryption and [deletion-retention.md](deletion-retention.md) for physical erasure.
