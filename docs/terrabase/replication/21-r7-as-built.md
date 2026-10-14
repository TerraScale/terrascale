# 21. Slice plan: R7 as built

**R7 as built** (2026-10-12; no storage format change, it uses `_tb_peers.applied/stable` and `_tb_state_dead`):

- **Frontiers.** For a database whose groups a peer has all acknowledged, the sender sends `Frontier(hlc)` (at most every
  5 s), with hlc taken before the read that found nothing to send. The receiver raises `applied[origin]` there and answers
  with its `StableReport`. Acks of batches carry `stable` too. Idle origins therefore advance stable stamps.
- **Stable and horizon.** Stable = min(`applied` over origins heard from in the last 24 h, own clock). Horizon =
  min(own stable, peers' reported stable) − 60 s, over the peers heard from in the last 24 h; a registered peer that never
  reported holds it at 0. A peer counts as gone (24 h) only once this process has itself been up 24 h
  (`LocalReplica.GoneBefore`), so a machine back after a long absence never mistakes its own silence for its peers'.
  CDC retention also uses this activity boundary, but its independent 24 h commit-age cut does not renew on restart;
  peers explicitly evicted from CDC retention (`seen = 0`) do not hold its floor during startup grace.
- **Collection.** With retention (every 5 s per database a peer stream reached): up to 1024 `_tb_state` rows with
  `live = 0` whose decoded entries all lie below the horizon are deleted.
- **No resurrection.** A refill now copies: the sender's horizon travels in `Refill`, and the refilled machine forgets
  every local state of a repaired bucket that the sender lacks and that lies wholly below that horizon (it was collected
  there). A machine back after the horizon passed thus drops a document others deleted and collected, instead of keeping
  it or bringing it back. Rounds need two consecutive differing rounds (60 s apart), while the refill runs at once on
  reconnect, so peers do not pull the stale copy first. A receiver-side "drop repairs below my horizon that I lack" rule
  was tried and removed: it cannot tell a collected state from a lost apply (the R6 repair scenario).
- Not in R7: collection of tombstoned fields and removed set dots inside live documents (section 11), GC of databases
  without peers (no stream reaches them, so nothing triggers collection), feed retention (R8), and re-stamping a
  returning machine's own unsent writes from before its absence (section 10, refill step 1).
