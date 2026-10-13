# 0013: A client write answers success only after a durable copy exists in another region

- Status: **proposed** (2026-10-13). The contract was approved by the user through COS; this record defines it for
  review before step 2 is coded. Owner: R6-FIX.
- Scope:
  - `Features/Replication`: the receiver's ack (`PeerStreamReceiver`, `StorageChangeGroupSink`) and the sender's acks
    (`PeerStreams`).
  - `Engine/Sqlite`: the sync settings of client databases (`SqliteDatabaseStore`).
  - `Features/Documents`: the client write path.
- Amends:
  - [0003](0003-sqlite-synchronous-normal.md): client database commits sync.
  - Design C ([multi-region-replication.md](../multi-region-replication.md) section 6, "ack after one node").
- Reuses from [0010](0010-write-wait-and-synced-durable-ack.md), which is superseded: the synced replica apply.
  Unlike 0010, a timeout is reported, never answered as success.

## Context

Under design C a write answers after one local commit, with `synchronous = NORMAL`. That causes three problems:

- A power loss of the accepting machine loses acknowledged writes.
- After such a loss, the machine's `turso_cdc` change ids restart below what peers already acknowledged. The sender
  then treats new groups as already shipped, so they are lost silently.
- The user's contract is: production writes succeed only after a durable copy exists in at least one other region.

## Definitions

- **Local durable commit.** The write's transaction committed on the accepting machine with `synchronous = FULL`: the
  commit returned after the write-ahead log was synced.
- **Remote durable commit of a change group on machine M.** The transaction that joined the group committed on M with
  `synchronous = FULL` (`BeginDurableTransactionAsync`), so its WAL is synced. Only after that does M ack. Receipt does
  not count, a queued group does not count, and WAL frames that are not synced do not count.
  - An ack of change id N on a stream means every group of the sender up to N is remotely durable on M.
  - A refill's ack likewise comes after its repairs committed durably.
- **Remote durable copy of a write.** A machine whose region differs from the accepting machine's, and whose region the
  database's placement includes, acknowledged a change id at or above the write's.

## Decision

### Step 1: the receiver acks only a remote durable commit

This already holds on main:
- `StorageChangeGroupSink.ApplyAsync` and `RepairAsync` join in `BeginDurableTransactionAsync` transactions
  (`synchronous = FULL`).
- The receiver acks after `CommitAsync` returns.

Step 1 pins this with an end-to-end power-loss test (`tests/TerraBase.E2E/WalFaults/ReplicatedApplyPowerLossTests`):
1. A real node runs under the write-ahead log fault shim and acknowledges pushed groups.
2. Its power is cut: every unsynced byte of every log is dropped.
3. After the restart, every acknowledged group must be present.

A mutation that applies in a plain transaction must fail that test.

### Step 2: the commit wait

1. **Which writes.** Every client write to a client database:
   - document `PUT`, `PATCH`, `DELETE` and `POST`;
   - `POST /transactions`;
   - collection create and drop, and collection schema set and delete;
   - index create and drop, and unique schema.

   These do not wait:
   - catalog and management writes (the system database, under the catalog primary, [0009](0009-catalog-replication.md));
   - database create and delete, backups and restore;
   - reads.

   Step 2 starts with documents and transactions. The DDL routes follow in their own PR.
2. **Precheck: unavailable before writing.** When no machine of another placed region has a connected peer stream for
   the database, the write is refused before it touches storage: `503` with code `remote_region_unavailable` and
   `Retry-After: 1`. It is definitely not applied, so retrying is safe.
3. **Local durable commit.** Client database write transactions commit with `synchronous = FULL`, as the system
   database's do ([0012](0012-system-database-synchronous-full.md)). The group commit lane shares one sync among the
   writes it batches. This also keeps `turso_cdc` ids from being reused after a power loss. The operation's record (see 5)
   is written in the same transaction.
4. **Wait.** After the commit, the target is the database's CDC head read right after the commit. That is at or above
   the write's own change id, so the wait is conservative. The write waits until a machine of another placed region has
   acknowledged at least the target.
   - The bound is **10 s**, a value in code: many cross-region round trips, since the sender is woken by the commit.
   - The wait holds no lock and no storage session.
5. **Operation ids.**
   - A write may carry `Idempotency-Key: <ULID>`. This replaces today's 400 "not supported yet". Without one, the
     machine mints a ULID.
   - Every write answer carries `TerraBase-Operation-Id: <ULID>`, the success and the ambiguous one alike.
   - The operation's record is a replicated document in the reserved collection `_operations`, written in the write's
     own transaction, so it travels in the same change group. It holds:
     - `request`: SHA-256 of the method, route and body;
     - `committed_at`;
     - `response`: status, plus a body without `document`.
6. **Success.** `2xx` as today, with `durability: "remote_durable"` instead of `local_durable`.
7. **Ambiguous outcome.** When the bound expires, or a link drops after the precheck, the answer is `503`:

   ```json
   {"error":{"code":"write_outcome_unknown","operation_id":"<ULID>","region":"gru","message":"..."}}
   ```

   With `Retry-After: 1`. The write is committed locally and keeps replicating, so it may become durable later. It never
   answers success.
8. **Idempotent retry.** A write whose `Idempotency-Key` names an existing record:
   - **Same request hash:** nothing is applied again. The machine waits as in 4, with the current head as target. It
     then answers the recorded response with `TerraBase-Replayed: true`, or the ambiguous `503` again.
   - **Different request hash:** `409 idempotency_key_reused`.
9. **Reconciliation.** `GET /v1/projects/{project}/databases/{database}/operations/{operation}` answers on the machine
   asked:
   - `200 {"operation_id","status":"applied","durability":"remote_durable"|"local_durable","response":{...}}` when the
     record is there. `remote_durable` runs the same bounded wait first.
   - `404 operation_unknown` when it is not.
     - Asked of the accepting region (named in the ambiguous answer; reach it with a region pin), a `404` means the
       write was never applied, or its record expired.
     - Asked elsewhere, a `404` only means "not here yet".
10. **Retention.** A record lives **24 h** after `committed_at`, the same horizon as CDC retention and refill. Then the
    accepting machine deletes it, and the delete replicates. A retry after 24 h is a new operation.

### Placement (open: COS decision needed)

Today a database created without `replica_regions` is placed in its home region only, so no other region can hold a
durable copy. The options:

- **(A, recommended)** Every database is placed in at least two regions:
  - A create without `replica_regions` places replicas in every other serving region.
  - A placement with one region is rejected (`400`) on create and update.
  - Every serving region can then confirm a write, which fits zero configuration and "every region always serves".
- **(B)** Databases placed in one region are exempt. They answer after a local durable commit with
  `durability: "local_durable"`. This keeps a weaker class of database under the same API.
- **(C)** A write to a database placed in one region always fails with `remote_region_unavailable`. This is literal,
  but it breaks every database created with the default placement today.

## API contract for the SDKs (ts-dotnet first, separate repository)

- **Request:** an optional `Idempotency-Key: <ULID>` on every client write. The SDK sets one per logical operation and
  reuses it on retries.
- **Response:** `TerraBase-Operation-Id` on every write answer. `TerraBase-Replayed: true` marks an answer that replays
  a recorded one.
- **`durability`:** `remote_durable` on every success.
- **Errors**, all with the standard error body:
  - `503 remote_region_unavailable`: not applied. Retry after `Retry-After`.
  - `503 write_outcome_unknown` with `operation_id` and `region`: applied locally, not confirmed. Retry with
    `Idempotency-Key: <operation_id>` and the same request, or reconcile.
  - `409 idempotency_key_reused`: the key names another request.
- **Reconciliation:** `GET .../operations/{operation}`, as in step 2.9.
- **SDK rule:**
  - Never surface `write_outcome_unknown` as failure or success. It is a distinct outcome, with the operation id.
  - Retry with the same key, at most until 24 h after the first attempt.

## Guarantees

For a write answered `2xx`:

- It committed with a synced log on the accepting machine.
- It committed with a synced log on at least one machine of another region holding the database. So it survives a
  power loss of the accepting machine, or the loss of its whole region.
- Its operation record travelled in the same change group, so a retry with its key, on either of those machines, does
  not apply it again.

## Not guaranteed

- **Serializability and linearizability.** Neither global serializability nor cross-region linearizability is promised.
  Concurrent writes still merge per field, last writer wins (sections 3 and 4).
- **Read-your-writes in other regions.** A read on a third region, or on another machine of the confirming region, can
  still miss the write until replication delivers it.
- **Ambiguous writes.** That a write answered `write_outcome_unknown` did not apply.
- **Idempotency.** It is not guaranteed for:
  - a retry with another key or with no key;
  - a retry routed to a region the operation's record has not reached. For example, the accepting region is cut off
    and the write never left it: a retry elsewhere applies again, and when the region returns both copies merge.
    `PUT`, `DELETE` and `PATCH` are idempotent by value, so this matters only for `POST` without a client id and for
    transactions;
  - a retry more than 24 h after the original.
- **Re-evaluation.** A replay answers the original outcome. It does not evaluate the request again against today's state.

## Consequences

- **Latency.** Every write adds one cross-region round trip, plus the remote fsync, plus the local fsync, which the group
  commit shares. The added time per write is measured and recorded in `benchmark/bench/`, before and after.
- **Storage.** Each write adds one `_operations` state row for 24 h, then a tombstone until garbage collection (R7).
- **Availability.**
  - A database's writes need its region and one other placed region to be reachable.
  - In a full partition, writes fail: `remote_region_unavailable` before the write, or `write_outcome_unknown` after
    it. None answers success.

## Delivery (separate PRs against main)

1. Step 1: the power-loss test and the ack definition in section 9 of the design.
2. Client database commits sync, with a before/after write benchmark.
3. Commit wait and precheck, the ambiguous answer, operation ids minted and returned, idempotent retry and
   reconciliation, for documents and transactions. Tests:
   - a remote crash before and after the durable ack;
   - a lost ack response;
   - the loss of one region (writes still succeed through the other);
   - a full partition (no false success);
   - rejoin and reconciliation;
   - a restart.

   Mutations: ack before the sync, success before the remote ack, a non-idempotent retry.
4. The DDL routes on the same wait.
5. The `api.md` and design-section updates land with the PR that changes the behavior.
