# Replication retention frontier caller brief

Status: read-only implementation brief for the next replication fixer. No
production code was changed for this brief.

## What can safely advance the storage consumer

The storage retention consumer is a local SQLite journal sequence. It must be
advanced from the minimum acknowledged *source journal sequence* across every
required admitted replication peer for one database scope. A causal origin
sequence is not interchangeable with this value: one publication carries both
its source journal checkpoint and its `(origin, origin_sequence)` causal dot.

`ReplicationRetentionFrontier` currently lives in
`crates/terrabase-server/src/replication_frontier.rs`. It correctly refuses
unknown peers, starts newly required peers at zero, takes the minimum, and
submits only monotonic advances to `Engine::advance_retention_frontier`. Its
caller must provide a source journal sequence, not an arbitrary causal clock
value.

## Physical RF2 owner and hook

For a local writer, the physical acknowledgement owner is the
`WalWriterDriver` in `crates/terrabase-server/src/replication_peers.rs`:

1. `TBWACK` is decoded at approximately lines 1003–1006.
2. `writer.acknowledge(&peer, ScopedWalAck::decode(...))` validates and folds
   the follower receipt.
3. The worker later reaches `WalCaptureStep::UpToDate` around lines 1068–1079,
   obtains `writer.durable_sequence(tenant)`, and calls
   `progress.advance_regional_from_peer`.

The retention hook belongs after the writer has accepted the WAL receipt and
has a verified durable source sequence. Do not use the sequence from an
unvalidated frame or the current engine head. Pass the exact acknowledged
source journal sequence to the frontier adapter. Keep the call outside the
per-frame synchronous storage path if possible: publish a bounded per-scope
candidate, and let the existing maintenance cadence submit the monotonic
minimum. A failed durable submission must leave the candidate pending.

## Causal remote-writer owner and hook

For each selected remote Region, the local writer creates one
`CausalDriver` in `production_data_runtime.rs` around lines 417–440. The
receive path is in `replication_peers.rs` around lines 1008–1032:

1. `CausalDriver::receive_frame` validates scope, placement, peer identity,
   receipt correlation, and the exact operation hash.
2. A `CausalReceive::Receipt` currently returns only `operations`.
3. The worker reads `causal.durable_peer_frontier()` and records it in
   `ReplicationProgress` as the remote Region's causal frontier.

That frontier is made of origin sequences and cannot directly advance the
SQLite retention consumer. The causal driver must additionally expose the
highest contiguous **source journal checkpoint** whose receipt has been
folded. The checkpoint is stored internally on each pending publication and
is moved to `self.cursor` when contiguous pending entries are acknowledged in
`crates/terrabase-tbep/src/production_causal.rs:40-53,406-418`. The next hook
should return a monotonic acknowledged source cursor from that fold, or an
explicit `Option<u64>` source sequence.

The current worker has a concrete correctness trap: in
`crates/terrabase-server/src/replication_peers.rs:1008-1032`, after
`CausalDriver::receive_frame`, it reads `engine.read_snapshot(...).sequence()`
and uses that value only for `ReplicationProgress`. That is the local engine
head at receipt time, not the source journal checkpoint acknowledged by the
remote causal peer. It must not feed retention. The retention hook for the
remote causal lane belongs on the sender-side `CausalReceive::Receipt` path,
using a new driver accessor that returns the cursor after the contiguous fold.
Do not derive it from `durable_peer_frontier`, the local snapshot head, or the
remote origin sequence: a publication may carry origin sequence 17 while its
source journal checkpoint is 43.

The physical path has a separate exact source value. At
`replication_peers.rs:1003-1006`, `TBWACK` is decoded and accepted by
`writer.acknowledge`; at `:1065-1090`, `writer.durable_sequence(tenant)` is
the verified local source journal sequence. Pass that `StorageSequence` (or
its checked `u64`) to the adapter only after the writer acceptance and durable
sequence query succeed.

## Ready implementation ownership

The smallest cohesive implementation spans these files:

* `crates/terrabase-server/src/replication_frontier.rs`: retain the existing
  required-peer/minimum state machine; add a caller-safe update API only if
  needed to defer the durable Engine call without losing a failed candidate.
* `crates/terrabase-server/src/replication_peers.rs`: call the adapter from
  the physical `UpToDate` branch with `writer.durable_sequence`; call it from
  the causal `Receipt` branch with the new CausalDriver cursor accessor.
* `crates/terrabase-tbep/src/production_causal.rs`: expose the post-fold
  acknowledged source cursor; keep repair publications (`checkpoint=None`)
  from moving the sequential retention cursor.
* `crates/terrabase-server/src/production_data_runtime.rs`: construct/admit
  one frontier per accepted `(DatabaseRouteScope, placement_generation,
  replica_epoch)` and register the authenticated physical and causal peer
  identities before worker receipt processing.

Do not make `ReplicationProgress` or `durable_peer_frontier` the retention
owner. They represent API/RF2 evidence and causal origin clocks respectively.

Do not derive this value from `durable_peer_frontier`, from the local engine
head, or from the remote Region's origin sequence. A receipt for a publication
with origin sequence 17 may acknowledge local journal sequence 43.

## Required-peer registration and replacement

Registration must happen only after the accepted assignment is validated in
`ProductionDataRuntime::build_peer_databases`:

- The local physical follower is one required peer for regional retention.
- Every admitted remote writer causal lane is another required peer if its
  source journal must remain replayable until that Region acknowledges it.
- The set must be keyed by authenticated member identity, scoped by
  `DatabaseRouteScope`, placement generation, and replica-set epoch.

The adapter must be initialized before workers can acknowledge progress, with
all required peers at sequence zero. This preserves fail-closed pruning during
startup and before the first receipt.

On worker/session loss, `PeerInbox::drop` fences progress and clears that
peer's observations around lines 1496–1514. The retention candidate for that
peer must likewise become unavailable or remain at its last safe value; it
must never be removed from the required set merely because the session is
gone. A replacement session in the same admitted epoch may repopulate the
same member key only after fresh authenticated receipts. A changed assignment
or epoch must create a new runtime/frontier owner and start all required peers
at zero. Never carry an old peer's cursor across a placement replacement.

## Scheduling and safety requirements

The hook should be a bounded in-memory update on acknowledgement, with a
single coalesced durable `advance_retention_frontier("replication", min)` per
scope per maintenance tick or monotonic change. The durable call must happen
through the owning Engine lease for that exact scope. Unknown consumer names,
scope mismatch, poisoned state, or a missing required peer fail closed and do
not advance the floor.

Required regression schedules:

1. Physical peer A acknowledges sequence 10 while required causal peer B is
   absent: durable replication frontier remains 0.
2. B acknowledges 7, then A acknowledges 12: frontier becomes 7; B then
   acknowledges 12: frontier becomes 12.
3. B's session drops after acknowledging 7: no later A acknowledgement may
   advance beyond 7 until a replacement B session acknowledges a newer source
   cursor.
4. A stale receipt from the previous epoch arrives after replacement: reject
   it and keep the new frontier at zero.
5. Duplicate and out-of-order causal receipts do not regress or double-submit
   the durable frontier.
