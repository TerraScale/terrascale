# Copy faults

Each database has one copy, on its home region's node. There is no peer to rebuild or refill it from: certified
rebuilds, source-pinned refill, bucket coverage and the operator rebuild-retry endpoint were removed with the
replication engine ([archive](archive/README.md)). Replica regions on Turso sync (R3) are being built in another
thread; until they ship, a faulted copy is recovered only from backup.

## The fault ledger

`Engine/Turso/StorageFaultLedger.cs` keeps two tables:

- `_tb_write_intent`, in the intent store: a FULL row naming the database, file incarnation and process replica before
  a write or copy of that file starts, removed once it ends.
- `_tb_fault`, in the SYSTEM database: one row per faulted database with a bounded reason.

After an unclean exit, startup reads every remaining intent of this process's replica before admitting work. An intent
becomes a fault ("the process exited uncleanly while this copy was written") unless its database was deleted or, on
Linux, its file and sidecars flush to disk without error and leave no page only in memory. This deliberately refuses
copies whose contents might have survived: a crash before the fault record must not silently restore trust. Startup then removes the intents.

An I/O or corruption error from an operation, a closing checkpoint, a rewrite or a session open records a fault for that
file incarnation. Failing to record a fault, or any fault of SYSTEM itself, fails the whole node closed.

## What a fault refuses

A faulted copy refuses writes and refuses to be the source of a database copy. A writer checks the ledger
before it starts and again after it takes the writer, and a commit batch that meets a fault fails every write in it.

- Document writes answer `503 write_outcome_unknown`; a durability-timeout response can follow a committed write, so
  clients treat it as uncertain and never replay it automatically.
- Other endpoints answer `503 copy_faulted` ("Storage requires recovery.") without exposing the fault reason.

Reads are not gated. Healthy databases on the same node keep working.

## Clearing a fault

Deleting the database removes its fault and intent rows with its files. To recover the data, restore it from backup
into a new database ([backup.md](backup.md#restore)); the restore creates a new incarnation with no fault. Nothing
clears a fault in place.

ts-auth stays unready while its storage copy is faulted ([signing keys](../auth/signing-keys.md)).
