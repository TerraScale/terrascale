# 29. A catalog gate opens from the maintained digests, and stopping never faults on the anchor

Amends the catalog gate's start (`CatalogGate`) and [row-derived repair digests](25-row-derived-repair-digests.md).

**Problem.** A restart served only once the gate had pulled the catalog from the other machines: 46-70 s after Kestrel
listened on 2026-10-28 (gru 56 s, fra 59 s, sjc 46-70 s), every request answering `503 catalog_unverified` meanwhile. Each
pull repaired 0 states. It began with digests recomputed from every state row of the system catalog on both machines (a
404 MB file on sjc), then read the buckets that differed only because writes were in flight. At stop, the final anchor
write ran on the host's stop token: once the stop budget ran out, the object store's cancellation escaped `StopAsync`
and the process ended with an unhandled exception (gru 14:57:07, fra 17:07:37).

**Rules.**

- A gate's pull compares the digests the engine maintains (`_tb_merkle`), on both machines (`DigestRequest.maintained`).
  A restored or behind copy keeps maintained digests that match its own rows, so it differs from its peers and is
  repaired exactly as before; only a row changed outside the engine needs the row scan, and the next anti-entropy
  round (which still compares the rows) finds it. A machine that predates the field answers from the rows.
- The anchor written at stop is best effort within the stop budget: a cancelled or failed write is logged and the last
  periodic anchor (at most 15 s old) stands. Nothing deletes or rolls back an anchor.
