# 21. Slice plan: R6 as built

**R6 as built** (2026-10-12; no storage format change):

- **Digests.** `Digest` returns a database's 4096 bucket digests: each the XOR of its documents' `_tb_state.h1/h2`, read
  in one pass when asked (`_tb_merkle` stays unused; fold it per commit once databases hold millions of documents). There
  is nothing to rebuild after an unclean start.
- **Rounds.** `AntiEntropy` runs every 60 s. For each database and each other machine whose region holds it, it compares
  digests and pulls (`ReadBucket`, pages of at most 1024 states and 2 MiB in an LZ4 envelope) the peer's states of every
  bucket that differed in two consecutive rounds. It joins them with origin 0: never shipped, no origin's `applied` moves.
  Both sides run rounds, so each pulls what it lacks. Metric `terrabase.replication.repairs` (documents changed, by peer
  region).
- **Retention cut and refill.** Retention also runs every 5 s without acks. A commit more than 24 h old stops being held
  by peer acknowledgements, including those of a removed peer or an active peer whose consumption stalls. This bound
  uses the persisted CDC commit time, so restarting the sender does not renew the hold. A peer whose unacknowledged
  CDC rows pass 1 GiB (measured once it is 65,536 rows behind) gets `seen = 0` and
  stops holding rows until it is heard from again. A sender whose CDC no longer holds every row the peer lacks after its
  ack sends `Refill(head)` instead of groups. Retention deletes a prefix, so that is `min(change_id) > acked + 1`, except
  over remembered quiet ranges (section 7): ranges proven to hold no local group, whose deleted rows the peer cannot lack,
  so the check runs at the end of the range holding the ack. A new peer at 0 is refilled only when local groups it lacks
  were cut; a sender whose CDC from 0 is proven quiet does not refill it, and anti-entropy seeds it. So R7's copy-refill
  of a machine back after more than 24 h cannot count on every sender sending `refill_required`. The receiver finds the sender by the machine name in its `Hello`, joins every bucket that differs at
  once, then acks `head`, and the stream goes on from there. The sender checks retention after reading groups, so a trim
  between the two shows as a cut.
- **Per-database order across a refill** (the changes feed relies on it). The refill runs beside the stream, so other
  databases' batches keep their 15 s ack. Until a refill of the database commits, its batches on that link are refused:
  while the refill runs, after it fails (acked refused, the sender asks again) and on the origin's next stream when the
  stream ends first. A newer stream of an origin starts only after the older one ended, refills included. One refill of
  a database runs at a time.
- **Refill admission.** One receiver admits at most four refills across all origins. Each reserves 16 MiB plus 128 KiB
  from the process bulk memory budget before reading digests or opening a peer channel, and keeps that reservation
  through repair completion or cancellation. Excess work is immediately refused, without an admission queue. Its
  database stays cut until a later refill succeeds; healthy databases keep flowing. The reservation accounts for two
  digests and a page's wire, decompressed, parsed and decoded payloads; it is not a measured bound on total managed heap.
- **Not copy-refill.** Without garbage collection (R7) merging old state is safe, so a refill merges. Once R7 collects
  tombstones, a machine away past the horizon must delete its file and copy from a peer (section 10), or it would bring
  collected deletions back. Per-database readiness during a refill is not built either.
