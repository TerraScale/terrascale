# System catalog invariants (R10a)

- Status: written by the catalog owner (R10a), 2026-10-12, for review and written acknowledgement by the R3/R6/R7 owner.
  Decision maker for deletion and retention: COS. COS DECISION: preserve catalog tombstones, catalog garbage collection
  OFF, until correctness of anything else is demonstrated.
- Scope: `_system` (the catalog) as a lattice database replicated to every region
  ([multi-region-replication.md](multi-region-replication.md) section 15, "R10a as built"). Client databases keep the
  R7 rules of section 11.
- Control tests: `tests/TerraBase.Fast/Replication/CatalogTombstoneControlTests.cs` and the chunked prefix delete's
  restart test in `tests/TerraBase.Fast/Management/DatabaseRecoveryTests.cs` (below).

## The rule everything else follows

**No catalog state is ever removed from a machine except by replacing it with a newer state.** Removal of a catalog
entry is a tombstone (a `DocumentState` whose clear is deleted), written like any write and replicated like any write.
Nothing collects, forgets, purges or truncates a catalog state:

| Path | Client database | Catalog (`_system`) |
| --- | --- | --- |
| Garbage collection (`CollectAsync` in the retention pass) | below the horizon | never: the pass skips the catalog |
| Stable stamp reported to peers (`StableAsync`) | from `_tb_peers` | 0, so no peer's horizon passes a catalog stamp |
| Horizon (`HorizonAsync`, sent with a refill) | from `_tb_peers` | 0, so a refill forgets nothing |
| Forgetting after a copying refill (`ForgetAsync`) | below the sender's horizon | refused |
| Stable stamps and frontiers received from peers (`RecordStableAsync`, `RecordFrontierAsync`) | recorded | recorded (bookkeeping only; nothing reads them for the catalog) |
| Prefix delete (`DeleteCollectionsAsync`: a deleted database's or project's state) | refused | a tombstone per live document, at most 1,024 per transaction (section 1) |
| CDC retention (`turso_cdc` rows) | acks, 24 h, 1 GiB | the same: rows are not states; a peer past retention refills |

"No horizon" means "never collect": every system-aware path of R7 opens the catalog, and the catalog answers 0 for
stable and horizon, so the same code collects nothing there.

## 1. Removed ids

- **Database.** Deleting a database (in its home region, after its replicas are removed) writes, in one catalog
  transaction: a tombstone of its record (`projects/<p>/databases/<id>`), a tombstone of its name index entry
  (`projects/<p>/database_names/<name>`), a tombstone of `backed_up_databases/<id>`, the storage sweep entry of its backups,
  this machine's cleanup intent, and this machine's prefix-delete intent
  (`machines/<machine>/pending_prefix_deletes/databases/<id>/`). The record's tombstone is kept forever (catalog GC is off).
- **Its state** (every live document under `databases/<id>/`: usage per machine and granularity, query statistics and
  shapes, backup policy and schedule) has no bound, so it does not go in that transaction (R3's objection 1: one catalog
  transaction holds at most 16,384 documents and 3 MiB). After the commit, `DrainPrefixDeletesAsync` tombstones it in
  transactions of at most 1,024 documents and 1 MiB of keys (a tombstone's state has a fixed size, so each transaction fits
  one change group). The transaction that finds no live document left removes the collection names and the intent. A
  failure or restart between chunks leaves the intent; the next drain resumes (after any deletion, at startup, and in each
  `CatalogConvergence` pass, every 2 s), and every committed chunk's tombstones stay. Between chunks the record is already a
  tombstone, so the rest of the state is unreachable: nothing reads a database's state without its live record. A project
  deletion does the same for `projects/<id>/`.
- **Limit:** the intent is this machine's. If the deleting machine never runs again, the untombstoned rest of the state
  stays live (and unreachable) on every machine. Open, with R11's machine replacement.
- **Id reuse: never.** Every id check (`create_database` with a chosen id, organization and project ids) reads the
  version, tombstones included, so a removed id stays taken: 409 `id_taken`.
- **Name reuse: yes.** The name index entry is a tombstone, which reads as absent; a new database of that name gets a new
  ULID, and a newer write of the index wins.
- **Organization, project, member, API key, invitation:** the same: tombstones, kept forever; ids stay taken.
- **Collections and indexes** are not catalog entries: they live in their own database (`_collections`, `_indexes`,
  `_unique` documents, section 5) and follow the client rules (R7 collects their tombstones below the horizon; a newer
  write brings a collection back).
- **Same-name race** (two regions create one name at once): the smaller ULID keeps the name on every machine; the larger
  one is kept under a conflict name, never deleted. See [database name conflicts](database-name-conflicts.md),
  `CatalogConvergence`.

## 2. Rejoin and refill

A returning machine (stream resumed from acks), a machine past CDC retention (copying refill), or a machine restored
from an older copy of its data:

- **No resurrection.** Every removal is a tombstone that every machine keeps, and a join keeps the newer clear: an older
  live copy (the returning machine's, the restored file's) cannot win against it, in a stream, a refill or an
  anti-entropy repair in either direction. A copying refill forgets nothing on the catalog (horizon 0).
- **No lost live entry.** Joins only add newer entries; nothing forgets or collects a catalog state; the returning
  machine's own writes ship from its CDC (stale or not), and anti-entropy compares every bucket both ways.
- **Restored catalog** (a machine whose `_system` is put back from a copy taken at time T): this is not "no
  resurrection, no lost entry" until a repair. Exactly what happens is in section 2a.
- **Prefix state** (R3's point 1: purge vs anti-entropy): a purge removed the purging machine's states without a trace,
  so a rejoining machine still holding them had them repaired back into the region that purged them. Decision (R10a):
  no purge; the prefix delete writes tombstones, which a repair from the rejoining machine cannot undo. The control test
  repairs from the rejoining machine into the deleting one and fails with the purge.

## 2a. A restored catalog, until its repair

Restoring `_system` on machine M from a copy taken at time T (correction after R3's review of `30bbd3f8`: an earlier
version said a document a machine held is never missing; that holds only without a restore).

- **What M lacks:** every catalog entry M had joined after T, whoever wrote it: its own writes after T, and peers' writes
  it acknowledged after T (the peers do not send those again). Peers' writes M had not acknowledged still arrive by stream.
- **What M would serve without a gate:** removed entries read live (revoked API keys, removed members, deleted
  databases and projects); entries created or changed after T missing or old; an id removed after T could be created again
  through M, and that creation, newer than the tombstone, would win everywhere. Anti-entropy alone ends that window in
  about 1 to 2 minutes (two differing rounds, 60 s apart), or a peer's later write of a key ends it for that key.
- **The restore gate (`CatalogGate`, COS DECISION 2026-10-12: fail closed).** Nothing on M can tell a restored file from a
  current one (a volume snapshot carries every local marker with it), so **every start is unverified**:
  - **Closed** from the start until M has completed one forced anti-entropy pull of `_system` (every differing bucket, at
    once) from **every other machine the member directory lists as started**, read successfully after this start (a static
    cell: the members it lists). A pull joins the peer's whole catalog, so M then holds every entry any listed machine holds.
  - **While closed:** every endpoint that authorizes (any endpoint that is not anonymous) answers 503 `catalog_unverified`
    before authentication, so no API key, token, membership or creation is decided from the catalog; the operator's
    `POST /admin/api-keys` answers the same; `/readyz` answers 503; `CatalogConvergence`
    decides nothing about files or names. Peer gRPC (streams, digests, bucket reads), probes, metrics and status run.
  - **Unreachable listed machine or a failed directory read:** stays closed, however long; passes retry every second.
    Never opens on its own.
  - **Operator override:** `POST /admin/catalog-gate/open` with `{"reason": "…"}` (internal listener only). It opens the
    gate and records a warning log and a catalog document `machines/<machine>/catalog_gate_overrides/<ULID>` (reason, time,
    machines not verified). Gauge `terrabase.catalog.verified` is 1 once open.
  - **Once open** it stays open until the next start; later differences go through streams and anti-entropy as before.
- **What the gate does not cover:** a machine that is not started (stopped or destroyed) is not waited for, so an entry
  that reached only such a machine stays missing on M until it starts and its own rounds bring it (its own start is gated
  in turn). An entry that never left M before the copy was taken is lost everywhere (unreplicated writes). Every start of a
  machine whose peers are listed but unreachable needs either the peers or the override.
- **Tests:** `CatalogLatticeTests.A_restored_catalog_serves_nothing_until_it_pulled_from_every_machine_then_refuses_the_revoked_key_removed_member_and_deleted_id`
  (restored sjc, gru unreachable both ways: sjc's catalog holds all three live, yet readyz, the revoked key, the removed
  member and a commit creating the deleted id all answer 503; once gru is reachable, sjc pulls and answers 401, 404 and 409.
  Without the middleware the revoked key answers 200: fails) and
  `CatalogLatticeTests.An_unreachable_machine_keeps_the_gate_closed_until_an_operator_opens_it_with_a_recorded_reason`.
- **Stale groups are joined (decision, this version):** a stale group (committed 24 h or more before it is read, whole
  states) from a peer can carry an entry M minted after T. R7's rule for client databases drops a stale state of a
  document missing locally that the machine held before (`HeldBefore`), because there it means a collected deletion; on M
  it means a restore, and dropping it would also drop the peer's own newer change until the repair. The catalog collects
  nothing, so `StorageChangeGroupSink` joins stale catalog groups like any other (`JoinStaleAsync` stays for client
  databases only).
- **Control test:** `CatalogTombstoneControlTests.A_restored_catalog_reads_its_lost_deletion_live_until_a_repair_and_joins_a_stale_change_of_its_lost_entry`.

## 3. Delayed and stale changes

- A change queued in a machine's CDC ships whenever it can, however old. There is no GC window on the catalog, so no
  catalog change is ever "older than the window" in the sense of section 11: it joins by the lattice rules. Older than a
  removal, it loses to the tombstone (the clear dominates older fields); newer than a removal, it brings back only its
  own fields (the document rule).
- A database record is written only by its home region (creation, placement, deletion), and one machine per region
  serializes those writes, so a placement change cannot be newer than a removal it raced. A multi-machine region (or R11's
  move) must re-check this.
- **R3's point 2** (the R7 rule that drops a stale change for a document missing locally that the machine held before):
  intended for collected tombstones. On the catalog nothing is collected, forgotten or purged, so the only way a held
  document goes missing is a restore (section 2a), and there dropping is wrong. The catalog therefore does not apply the
  rule: stale catalog groups join like any other (a catalog branch in `StorageChangeGroupSink`). If catalog GC is ever
  turned on, that branch must go with it, since the rule is what keeps a stale change from resurrecting a collected
  catalog entry.

## 4. File deletion

- **Home region:** `delete_database` records the cleanup intent and the record's tombstones in one catalog commit, then
  deletes the file, then clears the intent, then drains the state's prefix delete (section 1); startup recovery finishes
  an interrupted one. It does not wait for peers' acks: a deletion
  wins over writes still unshipped anywhere (the database is gone).
- **Every other region:** `CatalogConvergence` (every 2 s) deletes a local file whose record is a tombstone, as soon as the
  tombstone has arrived (stream, refill or repair). A missing record never deletes a file (a creation in progress, or the
  startup audit's unknown file). Because tombstones are kept forever, a region that returns after any absence still gets
  the tombstone and still deletes its file.
- **Placement removed** (a live record that no longer places this region): the file stays, not served, until R11's move
  drops it after the new region acknowledged everything (section 16 step 5).
- **Cleanup intents** are per machine (`machines/<machine>/pending_database_cleanup`): they replicate but only their own
  machine acts on them. An intent whose database the catalog registers (another region registered the same chosen id
  while this machine's creation failed) keeps the file and clears the intent.

## Control tests

`CatalogTombstoneControlTests.A_removed_database_stays_removed_after_garbage_collection_time_a_refill_and_a_stale_change_and_a_live_one_survives`:
two machines without peer streams, fake clocks, every change shipped by the test through the production source, sink and
replica-state calls, in a fixed order, without sleeps:

1. gru creates databases `removed` and `kept` (replica sjc) and writes `removed`'s backup policy; ships to sjc; sjc creates
   `removed`'s file.
2. sjc, away: changes `removed`'s record (queued, older than the removal) and creates `made-away`.
3. gru removes `removed` and deletes its file.
4. 25 h later gru runs the production retention and GC pass on the catalog.
5. Rejoin: gru's copying refill into sjc (with gru's horizon), sjc's queued groups into gru, gru's groups into sjc, an
   anti-entropy repair from sjc into gru, then each machine's convergence pass.
6. Both machines: `removed` is a tombstone, not read, not named, not registered, its file gone, its backup policy gone;
   `kept` and `made-away` are live and named; re-creating id `removed` answers 409.

Broken variants, each run locally against the same test:

| Variant | Result |
| --- | --- |
| This implementation | pass |
| Catalog GC on (catalog reports stable and horizon, forgets, and the pass collects it) | fails: gru no longer holds the record's tombstone |
| Prefix purge instead of tombstones | fails: the repair from sjc brings `removed`'s backup policy back into gru |

`CatalogTombstoneControlTests.A_restored_catalog_reads_its_lost_deletion_live_until_a_repair_and_joins_a_stale_change_of_its_lost_entry`
(section 2a), same two machines:

1. gru creates database `removed`, ships to sjc, and is stopped while its `_system` files are copied.
2. gru deletes `removed` and creates `created`; ships to sjc; sjc, now away, changes `created`'s record (queued).
3. 25 h later gru's `_system` is restored from the copy. Asserted: `removed` reads live on gru (the window).
4. sjc's queued group (stale) reaches gru. Asserted: `created` is on gru with sjc's change; `removed` still reads live.
5. An anti-entropy repair from sjc into gru and gru's convergence pass. Both machines: `removed` is a tombstone, not named;
   `created` is live with sjc's change and named; re-creating id `removed` on gru answers 409.

| Variant | Result |
| --- | --- |
| This implementation | pass |
| Stale catalog groups through `JoinStaleAsync` (no catalog branch in the sink) | fails at step 4: gru drops the group |

`DatabaseRecoveryTests.A_deleted_databases_catalog_state_larger_than_one_change_group_goes_in_chunks_that_a_restart_resumes`
(section 1): 17,000 documents under one database's state (more than one change group), the database deleted, a fault after
the first chunk that fails every later chunk (the deletion's own drain and every convergence pass), then two restarts.
Asserted: after the fault the record is unregistered, its file gone, the intent present, exactly 1,024 documents
tombstoned and the rest live; after each restart every document is a tombstone, the intent is gone, the record stays
unregistered and the other databases are intact.

| Variant | Result |
| --- | --- |
| This implementation | pass |
| The whole state in the deletion's transaction (before this version) | fails: the deletion hits the change-group capacity and the database is not deleted |
| The intent cleared with the first chunk | fails: the intent is gone after the first chunk, so nothing would resume the rest |

## Disagreements and decisions

- R3's `f82690b6` opened the catalog in every R7 path, and the R7-on-R10a merge `e9841c75` collected catalog tombstones.
  R10a disabled them (`cdcd019d`). COS DECISION: catalog GC OFF; R3 withdrew `e9841c75` and `f82690b6` and agreed with "tombstones
  kept, no horizon = never collect". This version keeps R3's shape (the paths open the catalog and record bookkeeping)
  and makes the catalog answer no stable stamp and no horizon, skip collection and refuse forgetting.
- R10a-2 first purged prefix state; R3's point 1 showed anti-entropy repairs it back. Changed to tombstones (this doc).
- Open, for a later slice: when a removed catalog id may be released (catalog GC), which then needs the R7 stale-change
  rule reviewed for the catalog and a deletion-aware file audit.
- R3's objection 1 (prefix delete past one change group, blocking) and correction to section 3 (restore): answered in
  this version (sections 1, 2a, 3); re-review requested.
- Open, outside the catalog: R7's intermittent
  `A_stale_queued_edit_of_a_document_deleted_and_collected_meanwhile_loses_to_the_deletion_everywhere` failure (stale
  edit ack), seen on this branch and on its base. Not fixed here and not claimed green by reruns.

## R3 acknowledgement

> R3 (R3/R6/R7 owner), 2026-10-12: acknowledged at 30bbd3f8. The catalog keeps every tombstone. No horizon means never
> collect (stable/horizon 0, forget refused, collection skipped), and every R7 path stays uniform. The prefix delete
> writes tombstones, which anti-entropy cannot undo. The control test passes, and it fails with catalog GC on (verified
> independently). Open: objection 1 (the prefix-delete capacity bound) and the restore caveat on the stale-change rule
> (section 3).

Re-review of objection 1 and the restore correction: _pending_.
