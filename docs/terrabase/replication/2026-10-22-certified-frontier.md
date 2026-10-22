# Certified refill frontiers

Certified refill carries the source's durable collected-state frontier in the same
snapshot as its bucket digests. After complete baseline coverage, the receiver
persists that frontier with FULL under its expected incarnation before publishing
the refill proof. Side-copy salvage and later repairs respect the inherited
frontier. Live baseline documents can predate that frontier.

Each source page also covers the receiver keys between the preceding and current
source cursor. The engine removes source-missing keys only when every entry is
below that source frontier, retaining newer state and joining the source page
with existing history. Missing collection metadata does not clear other baseline
keys. Empty tables are removed only when no member state or retained definition
needs them. An empty final page covers the remainder. Each local page
uses its own FULL writer with the expected persisted identity and Swapped phase,
so retained state admission stays bounded. A prior refill proof is revoked
durably before modifying its baseline. Original/additional side copies remain.

Baseline joins can import live documents older than the durable frontier; salvage
and ordinary repairs retain the frontier guard. A source frontier below an already
inherited receiver frontier is refused. If the source frontier advances during
coverage, the next bounded round covers all buckets at that newer frontier;
otherwise only differing buckets are retried. Ordinary serving writes remain
refused until certification completes.

The peer digest adds optional fixed64 `collected_before` field 5. New peers emit
presence even for zero. Older peers ignore it; new certified refill refuses a
source that omits it or supplies a value above Int64.MaxValue. During a rolling
deploy, certified refill therefore needs an upgraded source. Ordinary replication
continues to use its existing messages.

Needs format bump: the engine GC change adds `_tb_gc_floor` to both schemas, and
SYSTEM gains maintained `_tb_merkle`. The deploy lane must coordinate the format
reset before opening existing files. `StorageFormat.cs` stays unchanged. This
change adds no separate rebuild-ledger format or in-place migration.
