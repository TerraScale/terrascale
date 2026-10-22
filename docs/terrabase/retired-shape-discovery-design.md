# Durable discovery of retired shape handles

Issue: [#277](https://github.com/TerraScale/terrascale/issues/277).
Baseline inspected: `e7fa366ddb289f5612c93d316b316fb92c568151`.
Status: design only; database-wide cleanup is not implemented by this note.

## Scope decision

The existing request-driven retirement queue is not a durable inventory.
`ShapeRegistry.MaxRetired` bounds it to 1024 entries; restart, overflow and
registry eviction discard discovery information. `ShapeService.MaintainAsync`
only consumes it following shape activity. Collection deletion without another
shape request therefore cannot reliably schedule cleanup.

`_tb_shape_chunk` and `_tb_shape_snapshot` index objects by a hashed handle.
`ShapeHandle.ComputeCanonical` includes database incarnation, replica identity
and epoch, collection name and generation, and canonical filter in that hash.
Neither table stores the lineage needed to distinguish an obsolete handle from
a current filtered shape after restart. Absence from either in-memory registry
is not evidence of retirement. Age alone is not sufficient either: the newest
published snapshot of a current handle must remain available.

A complete implementation needs durable lineage publication at both object
writers, engine enumeration and transaction contracts, an independent scheduler,
race protection, and parallel integration coverage of four lifecycle cases.
Estimated review scope is 550–850 changed lines including tests. Split it into
the increments below rather than fitting an unsafe deletion rule into 400 lines.
No storage format bump or production deletion is authorized by this design.

## Durable inventory

Publish a versioned local derived lineage record in the same write transaction
as the first chunk or snapshot page for a handle. The record identifies the
handle, collection name and generation, database incarnation, and serving
replica identity and epoch. The canonical filter need not be stored merely to
test lineage: eviction does not retire a deterministic filtered handle while
those lineage fields remain current.

Use a separately reserved metadata key in the existing derived shape tables,
following the existing `~floor:` convention, after auditing every table reader,
mirror, counter, snapshot exporter and deletion path. Keep metadata distinguishable
from canonical `h1-` objects and version its payload explicitly. This is a proposed
encoding, not a new supported format. Bound metadata bytes before decoding and
never rewrite canonical sealed bodies. If that audit cannot prove isolation, use
a dedicated derived metadata table in the separately reviewed format lane instead.
Do not change `StorageFormat.cs` in a feature PR.

Enumerate lineage records, floor sentinels, and canonical handles across both
object tables, including snapshot-only and floor-only handles, through the engine
interfaces with indexed keyset pages. Join lineage without materializing object
bodies. A handle without valid
lineage remains protected and produces a bounded diagnostic; never guess that
it is obsolete. Existing unclassified derived objects require a separate reviewed
rebuild/reset decision, not an implicit migration or deletion fallback.

## Retirement transaction

For each candidate, re-read authoritative collection generation and database
incarnation within the write transaction that performs the deletion. Protect
replica identity/epoch transitions with the same lifecycle ownership used by
the object writer. A preflight check in a different transaction is insufficient.
Missing collections count as retired only after their committed deletion is
visible. Busy, unavailable, corrupt or cancelled reads do not prove retirement.

If lineage remains current, run ordinary retention and preserve every page of
the newest published snapshot: the greatest offset with a terminal page
(`next_id IS NULL`), not an incomplete newer offset. Otherwise invoke the bounded handle-drop logic,
including the floor sentinel. Retain the lineage record until both object tables
and the floor are empty. Publish and cleanup must use the same lineage validation
so a sealer cannot repopulate an obsolete handle after its cleanup transaction.
Pass lineage into every snapshot-page insertion and validate it in that writer
transaction; its long-lived source reader may predate a committed collection drop.

Keep the existing per-pass limits of 1024 rows and 8 MiB of object bodies. A
positive deletion count requires another maintenance turn even if it is below
the row limit. Cancellation rolls back the batch. Object URLs, sealed bytes,
published snapshot identities and retained-offset floor semantics do not change.
Local retirement does not authorize deleting mirrored Tigris objects or keys.

## Scheduling and admission

Register an always-on hosted maintenance service, independent of shape requests.
Discover tenant databases through `IStorageEngine.ListDatabasesAsync`, open them
through the engine, and take one bounded candidate page and deletion batch per
database per turn. Reuse existing database lifecycle handling; deletion or closure
of a database skips that database without failing the whole pass.

Reserve bulk memory before allocating candidate records, bound both page count
and bytes, and use existing tenant QoS admission before storage work. Release
admission between batches so a large retired handle cannot monopolize a database.
Advance a keyset cursor past current handles as well as completed retirements;
wrap it at the end so later retirements are rediscovered. Restart can begin a new
scan because the inventory and partially completed deletions are durable.

Use the host `TimeProvider` for scheduling and cancellation for shutdown. Log
errors without tenant-controlled metric labels, filter contents, credentials or
object bodies. Retry busy work on the next scheduled turn, with no feature flag,
operator tuning knob or unbounded retry queue. Keep the current request queue
only as a latency optimization until it can be removed without changing behavior.

## Review increments and acceptance evidence

1. Add the engine inventory contract and atomic lineage publication in chunk
   and snapshot writers. Test snapshot-only discovery and preservation of current
   filtered handles. Audit all reserved-key consumers before choosing the encoding.
2. Add the admitted, cancellation-aware scheduler and transactional retirement
   checks. Test collection deletion/recreation racing a sealer, partial batches,
   current newest-snapshot preservation during partial publication, floor-only
   discovery, and retained-floor behavior.
3. Add parallel `TerraBaseTestServer` integration scenarios for restart, retirement
   queue overflow, registry eviction followed by lineage change, and collection
   deletion with no subsequent shape request. Each owns its server, storage and
   clock. Verify cleanup independently through the engine after a scheduled pass;
   do not use wall-clock sleeps or another request to trigger maintenance.

Required Fast checks gate each increment. Run the four lifecycle scenarios 20
times on dev hosts and attach commit SHA, host, exact commands and per-test pass
counts before claiming the feature fixed. Include memory/QoS refusal and
cancellation evidence. No repeat or stress loops belong in CI.

Implementation owner: `mariogk` (repository maintainer), pending explicit scheduling.
Review/reassignment deadline: **2026-10-23 12:00 UTC**. This is a follow-up deadline,
not evidence that cleanup is already safe or implemented.
