# 11. Garbage collection

- **Stable stamp of a machine** = min over every origin in its directory of `applied[origin]`, where its own entry is
  its clock. Heartbeats advance idle origins. A directory machine never heard from counts as 0, so it holds GC. The
  machine reports `stable` in every ack.
- **Horizon** at machine X = (min over the database's replica machines of their last reported `stable`) − 60 s. When
  the horizon passes a stamp, every replica has applied every event at or below it, from every origin. Because each
  origin mints in commit order (section 3), no older event can still arrive.
- **Collected below the horizon**, in bounded lane batches (≤ 1024 rows) every 60 s:
  - `_tb_state` rows with `live = 0` whose newest entry is below the horizon (index `_tb_state_dead`);
  - tombstoned bases, but only when the field has no live counter entry or dot in that epoch. Removing the base would
    move the epoch back and kill them;
  - removed dots, except a removed explicit dot at `base.version` of an array base: it hides an implicit element of
    that base, so it stays as long as the base does.

  Dominated entries go at once through normalization (section 4).
- **Eviction.** A machine missing from the directory for more than 24 h leaves the horizon and the CDC retention. If it
  returns, it refills (section 10). A down region therefore holds tombstones and CDC for at most 24 h.
- **Feed retention.** `_tb_feed` rows older than 7 days are deleted and `_tb_feed_floor` advances. Change groups leave
  `turso_cdc` as in section 7.
