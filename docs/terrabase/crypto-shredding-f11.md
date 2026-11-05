# Database and tenant deletion (F11)

F11 implements PIVOT D19. A successful customer database DELETE destroys the database's envelope keys before returning
204. It returns 503 if the deletion committed but key destruction could not be certified; durable deletion work retries.
Project and account erasure apply the same permanent authority to every owned database. A database ID is never reused.

## Custody outside backups

Wrapped database envelope keys live only in the existing unsnapshotted R2 bucket `ts-terrabase-shapes-prod`, under
`v1/keys/<organization>/<project>/<database>/<key ULID>`. The system database uses `v1/keys/_system/`.
Tigris and customer buckets receive ciphertext, never wrapped envelope keys. Database page-key sidecars carried inside
snapshot bundles are themselves protected by the corresponding database envelope key. Restoring the system database
cannot restore a deleted customer's envelope key.

The existing bucket identity supplies CRUD. The public shape WAF rejects key paths before cache lookup, and custody
PUTs use `no-store`. There is no new credential, permission, bucket, setting or paid capacity.
The prefix must have no versioning, copy lifecycle, backup or replication job. R2 does not implement S3 bucket versioning
or bucket replication APIs. The live configuration inspection is recorded with the F11 evidence.

DELETE first writes a permanent `shredded` record. Key selection refuses that authority, including a generation racing
with deletion. It deletes every wrapped key in the database scope, performs an individual GET that must return 404,
then checks a fresh empty key listing. Missing custody access keeps destruction pending. Repeated passes preserve the
first deletion timestamp. In-flight operations that already obtained key material may finish; retained client keys and
plaintext already delivered to clients cannot be revoked.

This is a greenfield custody and push-format cutover. Old Tigris key objects and bucket snapshots are not a migration
source. The deployment lane owns the required format bump and reset; historical restore points from the old format
are outside the new guarantee and must not be presented as F11-protected.

## All persisted copies

| Copy | Protection and deletion |
| --- | --- |
| Primary and region database files, WAL, managed snapshots | Existing AEGIS-256 page encryption; permanent local cleanup closes handles and removes files and sidecars. |
| Continuous snapshots and logical segments | Existing TBK1 envelope; all destinations obtain keys from R2 custody. |
| JetStream regional push messages | TBP1 envelope under the same per-database key; deletion also purges the database subject from the local origin stream. |
| Regional partner holds and their system backups | The exact TBP1 ciphertext from JetStream; no decoded SQL is persisted in the hold record. |
| Shape objects in R2/CDN | Existing TBS1 epoch derivation under the database envelope key; deletion removes owned objects and queues their CDN purge. |
| Region key caches | Historical decrypt checks fresh custody authority before a cached key; deletion passes also discard local cached keys. |
| Tenant/project closure | Existing permanent closure enumerates every database and records individual deletion authorities. |

TBP1 adds 78 bytes: magic, data-key ULID, random 32-byte salt, and a 16-byte GCM tag. HKDF-SHA256 derives a unique
object key with the database identity and a transaction-specific domain. An envelope cannot be transplanted to another
database. The existing compression runs before sealing. One HKDF and one AES-GCM seal are added per transaction;
JetStream and the partner reuse the same bytes. A warm key selection performs no network operation. Cold key selection
and rotation access R2. Home consumption and partner takeover decrypt before applying; a shredded payload is discarded.
No read-pull behavior or quorum/durability policy changes.

## Convergence and CDN purge

Every serving node, including regional hold partners, scans the shared custody namespace in pages of at most 128
objects every two seconds. A tombstone becomes permanent local catalog deletion authority even after a partition or
restart. Catalog maintenance then closes the local database. With a healthy, single-page custody inventory, region reads
are fenced within two scan/maintenance ticks (normally under five seconds). Larger inventories take a complete scan:
`2 seconds × ceil(custody objects / 128)`, plus catalog and storage time. A partition has no finite completion bound;
its durable authority is applied when custody is reachable again.

The one-minute backup erasure pass additionally removes partner holds, purges JetStream subjects and scans owned shape
objects. It persists the R2 scan cursor and retries partial work. It does not certify physical cleanup before all local
files are absent. Healthy small-scope copy cleanup therefore normally finishes within one erasure interval plus I/O.
These are scheduling bounds, not a promise that a failing storage provider responds within them.

A CDN outbox entry is disk-synced before deleting an R2 object. A full outbox refuses the deletion so it can be retried.
The worker verifies that the R2 object is absent before purging its handle prefix, clearing all browser Origin variants.
Only a successful Cloudflare response removes the outbox entry. Restart, queue saturation and failed purge attempts
retain work. Each worker processes at most 100 records per batch, with at least twelve seconds between purge requests.
Purge is storage reclamation; key destruction is the confidentiality boundary.

## Proof

The Fast integration coverage retains one happy path (a real partner hold, an independent warm region cache and
post-delete decrypt rejection) and one critical failure (custody failure returns 503 and durable deletion retries).
The deployment SHA, live gru/sjc/fra reads, backup probes, CDN requests, synchronous custody 404s and synthetic-account
cleanup belong in `benchmark/bench/2026-11-05-f11-crypto-shredding.md` once measured.
