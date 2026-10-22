# 3. Identity, HLC and stamps

- **Node id** = `ReplicaId` (64 bits: region 16, member 32, epoch 16). The member number is random and lives in
  `.terrabase.replica` on the volume. The epoch rises on every unclean start (kv679 protection, unchanged). The region
  bits become the product region code as base 26 (`a`=0, so `gru` = 6·26² + 17·26 + 20 = 4518; three letters < 17,576).
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
- **Skew bound.** No OCI host-clock skew measurement is established here. Within clock skew,
  last-writer-wins between concurrent writes may pick the writer that is slightly earlier in
  real time. Past 1 s the metric alerts. The stored stamp is not rejected: accepting it at one
  replica and rejecting it at another would break convergence.
- **New origins.** Admission requires the node's own identity in the member directory and one
  heartbeat interval (5 s) before writes. The GC design relies on peers learning the new origin
  (section 11). Verify that the deployed membership transport supplies this admission contract;
  a hostname resolving or a node appearing in a peer list alone is not serving evidence.
