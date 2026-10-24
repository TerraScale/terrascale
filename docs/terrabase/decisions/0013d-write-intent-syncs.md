# 0013d: Reads record no write intent, and an ordinary intent commits without a sync

- Status: proposed (RT2 lane M, 2026-10-23; awaiting human review)
- Scope: `src/TerraBase/Engine/Turso` (`RebuildLedger`, `TursoDatabaseStore.Operations`, `TursoStorageTransaction`).
  Builds on [0013c](0013c-crash-restart-proof.md), which this record needs: without its restart proof, a lost
  intent would change what a restart does.

## Context

Write intents (`_tb_write_intent` in `_intents.db`) close the gap of [0013a](0013a-remote-durable-acknowledgement.md)
section 1.3: a process that dies after a failed sync, before its fault record, must not restart on the same boot and
serve pages the kernel never wrote. Every storage operation recorded an intent with FULL when the copy's first
operation began and retired it with FULL when the last one ended.

Measured on one node (`benchmark/bench/2026-10-23-rt2-vs-turso.md`), this cost:

- every isolated point read two FULL commits on `_intents.db`: engine read p50 3,533 µs against 11 µs for raw Turso;
- every isolated write about four syncs (intent, data commit, retire, and the retention pass's own write) where raw
  Turso does one.

## What an intent protects since 0013c

At the next start, an ordinary intent (any replica id but `Faulting`) of a copy that no record names is proven, not
faulted: its files sync without error, their cached pages are dropped, and none stays resident. Reads then come from the
disk alone. Only these cases fault the copy:

- a `Faulting` mark: a fault was noted and its record may not be durable;
- a rebuild record that names the database;
- a proof that fails (a sync error, a page still resident or mapped, a platform without `mincore`).

What a restart sees therefore depends on how the process ended:

- **Process crash** (SIGKILL, abort, `FailFast`). The kernel keeps every completed `write()`, so a commit made with
  NORMAL is read back exactly as one made with FULL. This is the ending the 0013a gap is about: the page cache
  survives it.
- **Power loss or kernel crash.** The page cache is gone. Nothing unwritten can be read back, and the proof passes
  for any copy whose disk syncs. An ordinary intent lost with the cache leaves the copy as the proof would have left
  it: serving from its FULL log.

## Decision

1. **A read-only transaction records no intent.** It runs `query_only`, so it neither writes nor syncs the file and
   cannot open the gap. Every write, copy, tap, checkpoint and writer maintenance still records one, as before.
2. **Damage a read detects is durable before its error reaches the caller.** Detection already notes the fault in
   memory and writes the `Faulting` mark with FULL (0013c). The transaction now waits for that mark before it rethrows.
   So a process that crashes right after a read fails still faults the copy at its next start. The mark is written
   without the ledger's gate, because rebuild barrier reads run under the gate and would otherwise wait on themselves.
   Recording an ordinary intent never downgrades a mark of the same incarnation. Only the fault's own record, once it is
   durable, turns the mark ordinary again.
3. **An ordinary intent is recorded and retired with NORMAL.** NORMAL writes the journal's log with no sync. The
   next FULL commit of the journal (a mark, a refused copy's intent) syncs it in order. This covers:
   - Process crash: every recorded intent survives and every retire stays retired, exactly as with FULL.
   - Power loss: a lost record or a lost retire changes nothing. A lost record leaves a copy that the proof would have
     passed anyway, and a lost retire leaves an intent that the proof passes.
4. **Stays FULL:**
   - the `Faulting` mark;
   - the intent of a copy a record refuses (under rebuild, or a known fault), so a genuinely partial copy is never left
     to the proof;
   - deletion, and recovery's own deletes.
5. **The retention pass is unchanged.** Its write runs every 5 s per database, not per write. Its intent is now NORMAL
   like any other, and its own commit stays FULL like every write transaction.

## Consequences

- An isolated point read does no sync. An isolated write does one sync: its FULL data commit. Measured counts are in
  `benchmark/bench/2026-10-23-rt2-intents.md`.
- The acknowledged-write contract (B1: every write transaction commits with FULL) is unchanged. No acknowledged write
  depends on the journal.
- No on-disk format change. Old and new binaries read the journal the same way. An old binary faults a leftover intent
  that a new one would prove.
- Depends on 0013c. On a platform where the proof always fails, a power loss that drops a retire faults a copy that
  FULL would have kept in service (fail closed, as before 0013c).
- Not changed here: a streaming query (`ReadBudgetedAsync`) does not classify damage at all, so it notes no fault. That
  gap predates this record.
