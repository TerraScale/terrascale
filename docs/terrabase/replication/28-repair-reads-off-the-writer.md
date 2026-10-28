# 28. Anti-entropy repairs read before they write

Amends the repair step of anti-entropy ([multi-region replication section 10](../multi-region-replication.md)). No
protocol, storage format or durability change.

**Problem.** A bucket whose writes are still in flight differs from the peer's in two rounds running, so every round pulls
it whole and joins each page of the peer's states in a durable write transaction. On gru (2026-10-28) almost every page
changed nothing (`terrabase_replication_repairs_total` did not move), yet each held the writer about 1.6 s, reading every
current state under it, every 2 to 3 s (`StorageChangeGroupSink.cs:124`). Client writes, applied batches and ts-auth's
requests queued behind it.

**Rule.** Each page is first checked in a read transaction: a state joins into something only if `WouldJoinAsync` says
the join changes the stored state (the same reads and clears as the join itself, without its writes). Only those go to
the durable write transaction, which joins them exactly as before; a page with none takes no writer. A state found held
stays held, because the stored state only grows by joins and a collection drop or the collected floor only grows what
they clear. States of the closure collections always go to the writer, which observes them. A row lost outside the
engine reads as absent, so its state still joins and is repaired.
