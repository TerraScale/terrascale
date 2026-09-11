# Replication retention owner audit

This is a read-only design audit; no product source or Cargo files were
changed.

## Cohort and source hashes

```text
HEAD: 8414011ef9f77312a1dae728a6d88383e84a2e29
d6a39ba462d42fbeaf37a17359a2d82eb106971f7b240ed8c1c8c39ec840907f  crates/terrabase-server/src/replication_peers.rs
2c4e25b50853a402131b325c76611b63fe0c53d032d0a2e2ef10150c843278df  crates/terrabase-server/src/replication_frontier.rs
```

## Causal ownership trace

`ReplicationPeers::retention` at `replication_peers.rs:182` is initialized at
line 319 and is never read through `self.retention`; the only other
`ReplicationPeers` construction is the synthetic test literal at
`replication_peers/admission_tests.rs:64-84`.

The actual frontier owner is not this field:

1. `start_inner` creates one `Arc<ReplicationRetentionFrontier>` at line 274.
2. It admits the immutable required peer set at line 276.
3. It moves a clone into `PeerOwnerContext` at line 299.
4. `spawn_peer_owner` passes clones into every `PeerWorkerContext` at lines
   586-598, including worker restart contexts at lines 737, 744, 747, 787,
   811, and 830.
5. Each authenticated session borrows that worker-owned Arc through
   `SessionContext` at lines 883-892.
6. Durable acknowledgements call `retention.acknowledge` at lines 1061 and
   1108. `ReplicationRetentionFrontier::acknowledge` computes the minimum of
   all required peers and calls `Engine::advance_retention_frontier` at
   `replication_frontier.rs:65-97`.

Thus retention admission and advancement are integrated through the worker
owner. `ReplicationPeers` itself exposes no retention accessor and no method
reads its field. The field's extra Arc clone does not own unique state: the
worker context and its restart context retain the same Arc, and worker/session
Drop releases only those clones after the owner stop/join path.

## Drop and production callers

`ReplicationPeers::drain` (`replication_peers.rs:543-558`) fences and joins the
peer owner. The owner task owns the active retention clone until its workers
finish. `ProductionDataRuntime::release` uses `peers.drain_completed()` at
`production_data_runtime.rs:301`; startup is through
`ReplicationPeers::start_with_dispatch` at line 332. Retirement uses
`capture_retirement` (`production_data_runtime/replication_retirement.rs:124`)
and does not access `self.retention`.

`ReplicationRetentionFrontier` has independent tests covering missing-peer
minimum preservation and assignment replacement refusal at
`replication_frontier.rs:118-140`. Peer admission/suspension tests exercise
the worker lifecycle and drain paths in
`replication_peers/admission_tests.rs`, including real suspension lease
release at lines 375-441.

## Decision

The field is dead storage, not a missing retention advancement integration.
The safe correction is mechanical:

- remove `retention: Arc<ReplicationRetentionFrontier>` from
  `ReplicationPeers`;
- remove only its initializer at `start_inner` line 319; and
- remove only the synthetic test literal field at
  `replication_peers/admission_tests.rs:65`.

Keep the import and all `PeerOwnerContext`, `PeerWorkerContext`, and
`SessionContext` retention fields: those are the live frontier ownership and
acknowledgement path. No `allow(dead_code)` is justified.

## Ready validation after the mechanical correction

Run the exact existing frontier tests and peer lifecycle tests discovered from
the test binary, at minimum:

```text
cargo test -p terrabase-server --lib replication_frontier --locked
cargo test -p terrabase-server --lib replication_peers::admission_tests::real_worker_suspension_releases_engine_and_rebinds_same_assignment --locked
cargo test -p terrabase-server --lib replication_peers::admission_tests::begin_drain_rejects_new_lanes_and_drain_still_joins_owner --locked
```

The correction must also leave `cargo clippy -p terrabase-server --lib
--locked -- -D warnings` with this `dead_code` diagnostic removed, without
changing retention frontier behavior.

## Approved implementation delta

Applied mechanically in the current worktree:

- removed the unused outer `ReplicationPeers::retention` field;
- removed its `start_inner` initializer; and
- removed the synthetic `ReplicationPeers` test-literal field.

Live `PeerOwnerContext`, `PeerWorkerContext`, and `SessionContext` retention
ownership plus both acknowledgement calls are unchanged. No tests were
added because the existing frontier minimum and worker suspension/drain tests
already cover the live owner path.

Post-edit source hash:

```text
124410462483fa7b5c69f1331217096df94c6cae96429f83f867d851756ce741  crates/terrabase-server/src/replication_peers.rs
```

`git diff --check` passed. `cargo fmt --all --check` could not complete because
an unrelated concurrent edit in `crates/terrabase-server/src/api_restore_selection_tests.rs`
has an existing mismatched delimiter (reported at line 185); no formatting
changes were made to that file.
