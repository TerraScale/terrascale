# Crypto-shredding of deleted databases

When a database is deleted, directly or through a project or account erasure, its backup copies become unreadable
right away. TerraBase destroys the database's data keys and leaves the old objects as they are.

## Keys

Continuous-backup snapshots, CDC log segments and cached shape chunks (TBK1 and TBS1 objects) are sealed under
**per-database** data keys. These are the same envelope keys described in [backup.md](backup.md#how-shipping-works):
TerraBase generates them, wraps them with AES-256-GCM under the OCI Vault master key (`TERRABASE_MASTER_KEY`) and
stores them in the bucket at

```
v1/keys/<organization>/<project>/<database>/<data key id>
v1/keys/<organization>/<project>/<database>/shredded      deletion record, once shredded
```

A project's keys are the keys of its databases, so erasing a project or account destroys all of them. `_system.db`
keeps `v1/keys/_system/`. There is no second encryption scheme. The database file's own page key, which a snapshot
bundle carries inside its sealed object, is unreadable once that object's key is gone.

## Deleting

A customer database deletion (`DELETE …/databases/{database}`, or a version 2 project closure after ts-auth erases an
account) records its permanent purge authority as before. Then, in every bucket the database shipped to (the platform
bucket and every customer destination it ever used):

1. TerraBase writes the deletion record `shredded` (`{"databaseId", "shreddedAt"}`) into the key scope. A record
   that already exists keeps its first timestamp.
2. It deletes every version of the database's wrapped keys, plus any project-wide key left over from before
   per-database scopes (`v1/keys/<organization>/<project>/<id>`).
3. It confirms with a fresh listing that no key remains.

The public DELETE starts this as soon as its deletion commits. Every node's erasure pass
(`DatabaseBackupPurge`, once a minute) repeats it until it succeeds. That pass covers project closures, other
regions' buckets and failed attempts. It runs before the pass waits for active-copy receipts, and the pass never
marks a backup erasure verified while keys remain (`keys_pending`). The catalog keeps the purge record's request time.
The erasure progress records `keysShreddedAt`.

Once a scope holds a deletion record, no member selects or generates a key for it, including customer shipping,
which otherwise always starts with a fresh key. A member still shipping a deleted database can seal for at most a
minute under a key it already had cached. Those objects are unreadable because the wrapped key is gone. Each node's
pass drops its cached copies of the scope's keys.

## Restoring

Point-in-time restore and historical snapshot reads check the deletion record first and answer
`409 database_shredded`, naming the deletion time. They give the same answer when a key read fails on a shredded
scope. A deleted database is no longer addressable through the `/v1` routes, so callers normally see `404` first.
The 409 is the answer for any internal or operator path that still holds its identity. Shape key requests for a
deleted database release no key.

The backup objects themselves are still deleted later by the existing [erasure pass](deletion-retention.md#backup-deletion),
after every holder confirms its copy is gone.

## Limits

- Customer buckets need **delete** on `v1/keys/<organization>/<project>/`. A bucket that refuses it keeps the erasure
  pending, and it keeps being retried, while the deletion record still stops new keys.
- Bucket-level snapshots or object versions that a provider keeps outside the S3 version API still hold old wrapped
  keys. They stay unreadable only while the master key is unavailable to whoever holds them.
- A client that already fetched a shape epoch key (`K_e`) can still decrypt the chunks of that epoch it holds.
- Local managed snapshots (`…/backups`) are files on the node, sealed with the database's page key. The storage sweep
  removes them; they are not off-node copies.
- Restore points shipped before per-database scopes name project-wide keys. They are not restorable after this
  change: every node start begins a new generation under the new scope, so only history from earlier generations is lost.
