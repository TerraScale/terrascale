---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# The two engines

TerraScale is two engines over one storage layer. Storage is mainline SQLite
(via `rusqlite`, exact version pinned, amalgamation build): one SQLite file per
customer database, WAL mode, one writer per file plus a read pool, group
commit. The publication journal is committed in the same transaction as the
write — in fact it is folded into the documents table, which is why
per-document history is free.

## The replication engine

The replication engine keeps every selected region serving writes and reads
for your database, with no leader region:

- **Inside a region**, the second copy (RF=2) is a physical WAL-frame follower
  of the writer's SQLite file — byte-identical, no logical replay. A write
  acknowledged `RegionalDurable` has been fsynced by that follower.
- **Across regions**, every region has its own writer per database. A journal
  tailer ships committed rows to peer regions over QUIC, in causal order.
- **Conflicts** are merged deterministically at the document level (per field,
  remove-wins, HLC tiebreak) and every merge is recorded in history with an
  explanation.
- **Anti-entropy** runs range-based set reconciliation on reconnect,
  bootstrap, and overflow, so any delivery order converges to identical bytes.

See [Replication model](/hi/concepts/replication).

## The sync engine

The sync engine turns writes into live query results without anyone's read
path touching your write path:

- A subscribable SQL query attaches to (or creates) a **canonical shared
  view**. Authorization-equivalent clients share one view, one delta
  computation, and one immutable log.
- On every commit, the affected views receive the row's delta through their
  compiled plan — no re-query. Once per 100 ms window, one delta per affected
  view is encoded (MessagePack) and appended to an immutable HTTP log.
- Readers long-poll that log on `sync.api.terrascale.tech`. A Cloudflare
  Snippet verifies the view capability and fetches through the CDN: a cache
  hit serves the same frozen, ETagged bytes to every reader with zero metered
  compute and no Worker on the path.
- Readers therefore scale with the CDN, not with your database: member load
  follows writes and distinct shared views, never attached readers.

See [Sync model](/hi/concepts/sync).

## What the two engines promise together

- Single-digit-millisecond local reads in every selected region.
- Local writes in every selected region with deterministic convergence.
- Live query fan-out bounded by writes and distinct views, not readers.
- A write billed once, regardless of how many views or subscribers it touches.
  Replicating a write to another selected region costs one write per region —
  shown up front, never hidden.
