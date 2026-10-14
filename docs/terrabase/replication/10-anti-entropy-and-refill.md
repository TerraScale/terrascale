# 10. Anti-entropy and refill

- **Digests.** A document's digest is the first 128 bits of SHA-256 over the length-prefixed collection and id,
  the state without `changed`, and the document bytes, kept in
  `_tb_state.h1/h2`. A bucket's digest is the XOR of its documents' digests, kept in `_tb_merkle` and updated from the
  lane's in-memory deltas at each tailer pass.
  - After an unclean start (the epoch rose), `_tb_merkle` is rebuilt by streaming `_tb_state (bucket, h1, h2)` before
    the first round.
  - The whole tree is 4096 × 16 bytes = 64 KiB, so one level suffices.
- **Rounds.** Every 60 s per (database, peer), and at every stream (re)connect:
  1. The node calls `Digest` and compares buckets.
  2. For each bucket that differs in two consecutive rounds (so in-flight streams settle), it calls `ReadBucket`
     both ways and joins the pages with `origin = 0`.
  3. A state whose entries are all below the receiver's GC horizon (section 11) and dominated, or already collected,
     is dropped instead of re-inserted. So repairs never bring back collected tombstones.
- **Refill** (a new replica, a machine back after more than 24 h, a peer told `refill_required`, a volume lost):
  1. Ship the machine's own unsent CDC groups first. These are real local writes with fresh versions, so they are
     safe to send.
  2. Delete the database file and create it empty.
  3. Copy all 4096 buckets from one up-to-date peer.
  4. Set `_tb_feed_floor[origin]` = that peer's `applied` vector at copy start, and set `applied` to it.
  5. Stream normally from there. `readyz` for that database stays false until step 3 completes.

  Refilling, not merging old state, is what keeps GC safe for a machine that was away.
- Today's `MerkleTree` (16 leaves, full scan), `AntiEntropyService`, `AntiEntropyWorker`, `AntiEntropyState` and the
  alpha proto are replaced.

**R8 key-aware digests** (storage format 7): `_tb_state.h1/h2` binds each state and materialized document to its
collection and id. Swapping states between keys in one bucket no longer preserves the bucket XOR when those states
are written under their new keys. Rebuild salvage verifies the same keyed digest and refuses a stored row moved to
another key with its old digest. Format 6 directories are refused; there is no migration. The state encoding and
public HTTP API are unchanged.

Anti-entropy compares stored digests; it does not recompute them to find a physical row move that preserves the stored
bucket and hashes. A detected divergence is repaired by lattice join: newer field versions win, so this does not
reconstruct the original values of an arbitrary swapped pair. Rebuild salvage's keyed verification refuses misplaced
rows before joining them.
