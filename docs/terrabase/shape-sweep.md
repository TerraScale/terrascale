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
truncated, skips every key sharing that prefix, and is classified unknown.

## Retirement

`SweepShapeHandleAsync` classifies and deletes in one write transaction:

| State | Condition | Action |
| --- | --- | --- |
| Unknown | Not a canonical v1 handle | Kept, counted |
| Unclassified | No lineage record (objects written before records existed) | Kept, counted |
| Malformed | Record over 256 bytes, nonzero columns, or not a valid version 1 body | Kept, counted |
| Current | Record equals the lineage read now | Kept |
| Retired | Valid record, and its collection is gone or its generation, incarnation or replica changed | One bounded drop batch |

Absence from the registry or age never retires anything; an unreadable incarnation fails the step. A
retired batch deletes objects first, then the floor sentinel once no object is left, then the record once
neither objects nor floor are left, so a partial drop stays classifiable and still refuses old offsets.
Because publication and retirement read the same lineage in their own writer transactions, a sealer
cannot repopulate a handle after its cleanup commits.

Ordinary retention of current handles stays request-driven and unchanged, except one fix: the newest
snapshot it always keeps is the newest *complete* one (`next_id IS NULL`), so an incomplete newer offset
no longer exposes the last complete snapshot to deletion.

## Scheduling and admission

`ShapeSweeper` is a hosted service, always on, independent of shape requests and the registry. Every
minute of the host `TimeProvider` it lists tenant databases (`IStorageEngine.ListDatabasesAsync`) and, per
database:

- reserves 16 KiB of bulk memory for the tenant and enters its QoS context per page, released before the
  next page; a refusal skips the database until the next pass;
- lists one page in a read transaction that ends before any writer starts;
- sweeps each handle in its own writer transaction, at most 1,024 rows and 8 MiB of bodies per database
  per pass; a batch that deleted rows continues on the same handle, and the next pass resumes there once
  the budget is spent;
- advances a per-database cursor past current, protected and finished handles, at most 64 pages per pass,
  and wraps it at the end of the inventory so later retirements are found again.

Cursors exist only for listed databases. Closed, deleted, busy or damaged databases are skipped and
retried next pass; shutdown cancels the pass and rolls back its open batch. The request-driven
retirement queue remains as a latency optimization.

Metrics (no tenant or shape labels): `terrabase.shape.sweep.handles{result}` with `current`, `retired`,
`unclassified`, `malformed`, `unknown`, `refused` and `failed`, and `terrabase.shape.sweep.deleted`. Sweep
reads count under `terrabase.storage.reads{path="shape_sweep"}`.

## Evidence

`tests/TerraBase.Fast/Shapes/ShapeSweepTests.cs` drives real servers with their own clock and storage:
restart with an empty registry after a drop with no later request, more retired handles than the
retirement queue holds in bounded passes, filtered-shape eviction then drop and recreate, protected
missing, damaged, oversized and unknown lineage, floor-only and lineage-only discovery, stale chunk and
snapshot writers rejected after a drop, cancellation rollback and memory refusal.
`ShapeRetentionTests` covers the complete-snapshot fix.

## Gaps

- Legacy objects without records stay unclassified until a writer adds a record to the same handle;
  removing them needs a separate reviewed rebuild or reset decision.
- QoS admission is the tenant's bulk memory share and QoS context. Request-rate admission
  (`IQosAdmission`) is not used: listed databases carry no plan, and a background pass must not spend a
  tenant's request tokens.
- Changing the incarnation in place is not exercised by the tests; it takes the same comparison as a
  generation change.
