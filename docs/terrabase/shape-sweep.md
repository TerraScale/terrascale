# Shape sweep: durable discovery of retired shape handles

Issue: [#277](https://github.com/TerraScale/terrascale/issues/277). Implements
[retired-shape-discovery-design.md](retired-shape-discovery-design.md), whose "not authorized"
status the maintainer lifted for this change. Mirrored Tigris objects, object URLs, sealed bytes
and retention floors keep their meaning; no storage format bump and no migration or reset.

## Lineage records

Each handle with local objects has one reserved `_tb_shape_chunk` row, next to the existing
`~floor:` and `~frontier` sentinels:

| Column | Value |
| --- | --- |
| `handle` | `~lineage:` + canonical `h1-` handle |
| `offset`, `end_offset`, `sealed_at` | `0` |
| `body` | ASCII `1 <incarnation> <replica> <generation> <collection>`, at most 256 bytes |

`1` is the record version. `incarnation` is `commit_offset.incarnation`, `replica` the decimal
replica id (member and epoch) of the serving process, `generation` the collection's `entity_id`.
These are the lineage fields `ShapeHandle.ComputeCanonical` hashes; the filter is not stored, because
eviction does not change a filtered handle whose lineage fields are current.

Both object writers publish the record in the write transaction that inserts their object: the chunk
sealer and every snapshot page insert. Each reads the current generation and incarnation in that
transaction and refuses (`ShapeSealOffsetException`, answered as `must_refetch`) when the shape's
definition names another lineage. The snapshot page writer gets the definition, not only the handle,
because the long-lived reader that built the page may predate a committed drop. The replica id is
fixed for the process lifetime, so it comes from the writer's own replica.

### Reserved-key audit

Every other consumer selects `_tb_shape_chunk` and `_tb_shape_snapshot` rows by an exact canonical
handle (`h1-` and 16 hex digits), which never starts with `~`: chunk reads, fresh bodies, snapshot due
checks and pages, retention trim, the mirror (`ShapeMirror` reads chunks, pages and floors by handle),
the durable frontier (`~frontier` only). Storage-quota usage sums document bodies only. Shape writes,
records included, run with capture suspended, so they are never replicated or shipped to backup.
Copies, backups and restores skip both tables (`TursoCopy`), so records never leave the file. Deleting a
collection or database leaves derived rows for the sweep, or removes the file. No canonical body or URL
changes.

## Inventory

`IShapeSweepTransaction.ListShapeHandlesAsync` returns up to 16 handles after a cursor, in key order, from
five indexed seeks per handle on the `(handle, …)` unique indexes: canonical and other client-range chunk
keys, `~floor:` keys, `~lineage:` keys, and snapshot keys. It reads at most 64 characters of a key and no
body, so floor-only, snapshot-only and lineage-only handles are all listed. A longer key is listed
truncated, skips every key sharing that prefix, and is classified unknown. SQLite orders keys by UTF-8
bytes and the listing compares UTF-16 code units, which differ only for non-handle keys (supplementary
characters, the truncation suffix U+10FFFF): the listing stops at a key that does not advance, and the
scheduler wraps its cursor instead of repeating the page. No such key is ever deleted.

## Retirement

`SweepShapeHandleAsync` classifies and acts in one write transaction:

| State | Condition | Action |
| --- | --- | --- |
| Unknown | Not a canonical v1 handle | Kept, counted |
| Unclassified | No lineage record (objects written before records existed) | Kept, counted |
| Malformed | Record over 256 bytes, nonzero columns, or not a valid version 1 body | Kept, counted |
| Current | Record equals the lineage read now | One bounded ordinary retention trim |
| Retired | Valid record, and its collection is gone or its generation, incarnation or replica changed | One bounded drop batch |

Absence from the registry or age never retires anything; an unreadable incarnation fails the step. A
retired batch deletes objects first, then the floor sentinel once no object is left, then the record once
neither objects nor floor are left, so a partial drop stays classifiable and still refuses old offsets.
Because publication and retirement read the same lineage in their own writer transactions, a sealer
cannot repopulate a handle after its cleanup commits.

Legacy floor sentinels and objects without a record stay unclassified: neither the trim nor the drop
touches them until a writer adds a record to the same handle or a separate reviewed reset removes them.
The replica in a record is the process's fixed replica id; after an unclean restart under a new epoch,
older records name another replica, so their handles retire in the same bounded batches.

## Ordinary retention of current handles

A current handle gets the same trim a shape request runs (`TrimShapeAsync`, which now shares its body),
in the writer that classified it: chunks sealed before `now - 7 days` (the feed retention, computed once
per pass) or below the floor go after the floor is raised over them, then snapshots below the floor and
that old, except every page of the newest *complete* snapshot (`next_id IS NULL`), so an incomplete newer
offset never exposes the last complete one. Retention therefore no longer depends on requests.

When the trim raised the floor, including through feed holes with no row deleted, the sweep hands the floor
to the mirror (`ShapeMirror.FloorTrimmedAsync`) after the writer commits and is disposed, as a request
pass does; the mirror only queues it, bounded by count and memory, and its worker rereads the floor before
deleting mirrored objects. Retired handles are never handed to the mirror: their Tigris objects stay.

## Scheduling and admission

`ShapeSweeper` is a hosted service, always on, independent of shape requests and the registry. Every
minute of the host `TimeProvider` it lists tenant databases (`IStorageEngine.ListDatabasesAsync`) and, per
database:

- resolves the organization's plan from the tenant directory first; while it is unavailable the database
  counts as refused and keeps its cursor;
- reserves 16 KiB of bulk memory for the tenant under that plan and enters its QoS context per page,
  released before the next page; a refusal skips the database until the next pass;
- lists one page in a read transaction that ends before any writer starts;
- sweeps each handle in its own writer transaction under one budget per database per pass, 1,024 rows
  and 8 MiB of bodies across trims and drops together; the remaining budget is checked before every
  writer and passed to it, a batch that deleted rows continues on the same handle, and the next pass
  resumes at that handle once the budget is spent or a body does not fit what is left;
- advances a per-database cursor past current, protected and finished handles, at most 64 pages per pass,
  and wraps it at the end of the inventory so later retirements are found again.

A drop charges the floor and lineage bodies it deletes to the same budget, after the objects; a record that does
not fit what is left stays for the next batch. A batch that finds a damaged row (a body over the 8 MiB object
bound, a floor or lineage record's included) rolls back; the handle counts as
malformed and the cursor moves past it, so it never pins the database. Cursors are kept only inside an
inventory and only for listed databases. Closed, deleted, busy or damaged databases are skipped and
retried next pass, also while shutdown is underway; only the sweep's own cancellation stops it, and it rolls
back the open batch. The request-driven retirement queue remains as a latency optimization.

### Request admission exemption

The sweep does not take request admission (`IQosAdmission`). Request slots and tokens are for requests: a
background pass would spend them and could be blocked behind a customer's exhausted quota, which must
never stop cleanup, and admitting a listed database would apply a placeholder plan that rewrites the
organization's lane and can reset a paid burst. The sweep is bounded instead by the storage engine's
`TursoIoPool` (plan limits, a fair per-organization weighted queue, the QoS context captured per
transaction), the tenant's bulk memory share, one database and one transaction at a time, listing pages of
at most 16 handles from 5 indexed `LIMIT 1` metadata seeks each and at most 64 pages, 1,024 rows and 8 MiB
per database per minute, with memory released between batches.

Metrics (no tenant or shape labels): `terrabase.shape.sweep.handles{result}` with `current`, `retired`,
`unclassified`, `malformed`, `unknown`, `refused` and `failed`, and `terrabase.shape.sweep.deleted`. Sweep
reads count under `terrabase.storage.reads{path="shape_sweep"}`; trims count under `path="shape"` as a
request's do.

## Evidence

`tests/TerraBase.Fast/Shapes/ShapeSweepTests.cs` drives real servers with their own clock and storage:
restart with an empty registry after a drop with no later request; 1,025 durable retired handles (one
real seal, the rest written with its lineage record and never tracked by the registry) in passes of
exactly one shared 1,024-row budget; scheduled retention of a current handle with no request (expired chunk
removed, floor raised, older complete snapshot trimmed, every page of the newest complete one kept beside a
newer incomplete one); a damaged handle protected while the sweep retires the next one; filtered-shape
eviction then drop and recreate; protected missing, damaged, oversized and unknown lineage; floor-only and
lineage-only discovery; stale chunk and snapshot writers rejected after a drop; cancellation rollback and
memory refusal. `ShapeRetentionTests` covers the complete-snapshot fix on the request path. The affected
Fast classes ran five fresh times on the exact commit, as the maintainer directed.

## Gaps

- Legacy objects without records stay unclassified until a writer adds a record to the same handle;
  removing them needs a separate reviewed rebuild or reset decision.
- Request admission is exempt by design (see above); no test exercises an unavailable plan, whose path
  counts the database as refused before any storage work.
- The 1,025-handle test does not fill the registry's retirement queue itself; it proves the sweep needs
  no queue or registry entry for more durable handles than the queue holds.
- No test observes the mirror floor notification: Fast servers have no Tigris mirror, so the callback is a
  no-op there.
- Changing the incarnation in place is not exercised by the tests; it takes the same comparison as a
  generation change.
