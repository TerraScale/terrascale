# 3. Identity, HLC and stamps

- **Node id** = `ReplicaId` (64 bits: region 16, member 32, epoch 16). The member number is random and lives in
  `.terrabase.replica` on the volume. The epoch rises on every unclean start (kv679 protection, unchanged). The region
  bits become the Fly region code as base 26 (`a`=0, so `gru` = 6·26² + 17·26 + 20 = 4518; three letters < 17,576).
  A node id is therefore unique per machine, per epoch.
- **Stamp** = `Hlc` (48-bit Unix ms, 16-bit logical), unchanged. A version is `(stamp, node)`, compared
  lexicographically. Two different events never share a version. A node mints each stamp at most once. A new epoch
  is a new node, so stamps lost in a crash are never reused.
- **One operation = one stamp.** A document write, a whole `POST /transactions` request, a SQL statement chunk
  (section 12) or a management commit gets one stamp `S`. Every entry it writes carries `(S, self)`.
- **Minting.** The write lane mints `S = clock.Tick(maxStamp(doc))` for each touched document. The clock atomically
  retains the uncapped maximum of its last mint and the document's stamp, then advances past it. Stamps are
  minted inside the single writer, so an origin's commit order equals its stamp order. The changes feed and GC rely on
  this (sections 11 and 14).
- **Observing.** Every applied stamp goes through `MemberClock.Observe`, which caps it at now + 1 s (`MaxSkew`, a code
  constant) and counts `terrabase.clock.skew_capped`. The stored stamp is never changed. `Tick(maxStamp(doc))` keeps
  read-your-writes even after a remote stamp from the future.
- **Opening.** Before publishing a system or tenant store, restore the clock's uncapped floor from its largest stored
  state stamp; tenant stores also include retained feed stamps and retention floors. Opening another store can only
  raise the shared member clock. This preserves durable local stamps across a clean restart with the same origin.
- **Skew bound.** Fly hosts run NTP. We expect skew of a few ms, though nobody has measured it on Fly yet. Within the
  skew, last-writer-wins between concurrent writes may pick the writer that is slightly earlier in real time. That is
  the documented behaviour. Past 1 s the metric alerts. Nothing rejects the stamp, because rejecting at one replica
  and not another would break convergence.
- **New origins.** A machine accepts writes only after two things: its own id appears in `vms.ts-terrabase.internal`
  and one heartbeat (5 s) has passed. By then every peer's directory has listed it, so GC holds for it (section 11).
