# 0013f: A write intent lingers one idle second before it is retired

- Status: proposed (RT2c lane P3, 2026-10-23; landed by RT2d for Q-PARITY, 2026-10-24; awaiting human review)
- Scope: `src/TerraBase/Engine/Turso/TursoDatabaseStore.Operations.cs` and the healthy rewrite (`TursoDatabaseStore.Rewrite.cs`). Builds on [0013c](0013c-crash-restart-proof.md)
  and [0013d](0013d-write-intent-syncs.md); changes when an ordinary intent is retired, nothing else.

## Context

Since 0013d an ordinary intent is recorded and retired with NORMAL, but still once per busy period: the copy's first
active operation records it and its last one retires it. A client writing one document at a time ends its only
operation with every write, so each write ran two more transactions on `_intents.db` (record and retire, each with
`BEGIN IMMEDIATE`, the schema check, the statement and `COMMIT`) and
two hops through the I/O pool, beside its one data commit. Measured with a statement count on one node: 2,837
`BEGIN IMMEDIATE` for 944 writes.

## What a lingering intent changes

An intent's presence matters at one moment only: the next start after an unclean exit. Then an ordinary intent of a copy
that no record names is proven (0013c): its files sync, their cached pages are dropped, and none stays resident; anything
else faults the copy. While the process lives, nothing reads the row.

So keeping an intent after the last operation ended is the same as an operation still being active at that moment:

- **Process crash** within the linger: the restart proves the copy, as it would after a crash during a write. Every
  acknowledged write is in a FULL commit, so the proof passes and the copy serves from its log.
- **Power loss** within the linger: the cache is gone, and the proof passes for any copy whose disk syncs (0013d).
- **Clean shutdown**: the closing checkpoint retires an idle copy's intent before the store closes, as before.

No acknowledged write depends on the journal (B1: every write transaction commits with FULL); this only makes a restart
prove a copy that it would otherwise have trusted.

## Decision

1. When a copy's last active operation ends, its ordinary intent stays recorded for `IntentLinger` (1 s). An operation
   that starts in that time records nothing: the lingering intent covers it. The retire runs once the copy has stayed
   idle for the whole linger, under the same `_operations` gate as every record and retire.
2. A copy a record refuses (under rebuild, faulted, unsettled) records its intent with FULL at every first operation,
   exactly as before, whether or not one lingers; its retire still keeps the row (`RetireIntentAsync`).
3. A faulted copy or a failed-closed ledger retires nothing, as before.
4. A closed store retires nothing late: its row could otherwise be deleted under a store of the same incarnation opened
   since. The closing checkpoint (`CheckpointOnCloseAsync`) retires a held intent first and makes every later last
   operation retire at once, so a clean shutdown leaves no intent; a deleted database's intent is forgotten with FULL as
   before.
5. A retire that fails fails the ledger closed, as before; it no longer fails the operation that ended last, which has
   already returned.
6. A healthy rewrite gives the same store a new incarnation. It retires a lingering intent while it holds the operations
   gate with none active, before the swap, so the next operation records an intent for the new incarnation.

## Consequences

- One client writing back to back runs one data commit per write and no journal transaction; a write after an idle
  second records the intent once more.
- A process crash or power loss within a second of the last write proves the copy at the next start (sync, drop the
  cached pages, `mincore`) instead of trusting it. On a platform where the proof always fails, that copy faults where an
  immediate retire would have kept it in service (fail closed, as 0013d already accepts for a lost retire).
- No on-disk format change; old and new binaries read the journal the same way.
