# Crypto-shredding of deleted databases

A successful database DELETE destroys its wrapped envelope keys before returning 204. If custody cannot confirm
absence, DELETE returns 503 and durable erasure work retries. Project and account deletion apply the same authority
to every database they own.

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

See [backup.md](backup.md) for backup encryption and [deletion-retention.md](deletion-retention.md) for physical erasure.
