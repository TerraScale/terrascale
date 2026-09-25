# Certified receiver apply and exact inbox replay

Status: **design / NOT IMPLEMENTED**. This is the bounded receiver packet after
[distributed-document-write-apply.md](distributed-document-write-apply.md).
The accepted D0/D5/D6 grammar and durability order in
[document-state-v2.md](document-state-v2.md), the exact replay contract in
[replication-v2-replay.md](replication-v2-replay.md), and the certified open
boundary in [distributed-document-open.md](distributed-document-open.md)
control. Packet-A source code exists in a frozen snapshot but its build and
runtime gates are pending; this brief does not accept it, connect the server,
or authorize `AppliedAck`. The current server continues to send only
`DurableAck` and `applied_sequence: None`.

## Pinned evidence and ownership boundary

TerraBase HEAD is `352d9f61185d1b685e3032519579040d169ad47d`.
The reviewed Packet-A snapshot at
`target/verification/tb-packet-a-mode-c0-20260924-source-snapshot/` has a
94-entry `source.sha256` manifest whose SHA-256 is
`91cce7d61dde56985f30594eb734b2981f14e1fd0d82896b8fbca5b035a3a3dd`.
The following are SHA-256 identities of the inspected **uncommitted working
tree** on 2026-09-24. `distributed.rs` still matches the frozen Packet-A
snapshot. `distributed_state.rs`, `distributed_producer.rs` and `lib.rs`
changed repeatedly during this review, so their immutable reference is the
frozen manifest above (respective snapshot hashes
`8d58758be591f95bf3b5861438de65460c86d8c26dc0d30e87e07181e7a05478`,
`dc7991d5b19f46f3c2bce0eb445ab024fa3c56a0633a16f5fd7b46637b177dc3`,
`8c6558688a9272ec38dff82772306e3866aa463922e7ece11b544e5ff94d3428`).
Their live audit, resend and causal-merge seams were also reinspected;
refresh these moving files before implementation.

| Path | SHA-256 | Current seam or limit |
| --- | --- | --- |
| `docs/distributed-document-write-apply.md` | `ffea29394dc36763ed5570f7676108809d63de37134596c7d6c4cd7f43730164` | Packet A/B/C ordering |
| `docs/document-state-v2.md` | `22ef94c55efa856422b8ec033192ccbd68c8cc122c20447e6878dc4ad4965992` | exact final frame and crash order |
| `docs/replication-v2-replay.md` | `c87b9f6634786df1bfb56edac0b473bd0cd4c508bd45ccf01341cff29441cf49` | journal replay authority |
| `docs/distributed-document-open.md` | `23a77e39d11eab93ef9f07c3210db18c6d55ca206822bbeb7862dea7aaca59ac` | authority-bound open |
| `crates/tb-document/src/distributed.rs` | `4d22534be5f3fb9d7f4badd6f84f8e9a119e8104dae6f587e94c884f88d1146e` | retained authority, writer gate, document storage |
| `crates/tb-replication/src/authority.rs` | `867eb8293903c7fa622928305e3b4c40aa0dbe30f98857b15a9194df6db3c0ed` | `CertifiedEvent`, `CertifiedReplayEvent`, `ReplicaReceiver` |
| `crates/tb-replication/src/metadata.rs` | `2c8a8b6d9d9d8921cb386b99a10f1962cc31ac7bf95badc56640855c6630e8e3` | proof-owned exact `VerifiedSourceChange` D1 slices |
| `crates/tb-replication/src/journal.rs` | `d23b140fbfedad340afaf6a8519fda50b8070176d354a29cb3bea5ee95c530a0` | bounded `read_durable_exact`, synced marker, private raw inbox |
| `crates/tb-replication/src/event.rs` | `56576aae259b07ca806428ab6da70412ba1549be5cdc7ecc4cf85a4dcad77667` | exact own-causal prefix |
| `crates/tb-format/src/document_v2.rs` | `da73c94766d11f4b0be7194a6535c72e9bf8d5baef5ddb24af45388cfb30f7da` | D0/D5/D6 and descriptor codecs |
| `crates/tb-storage/src/local.rs` | `22d5ec284fa8c1d579ca3185e268ccd11556dd39a2beb0297f6cc57e99d33e73` | whole-stage reservation, `prepare_final` held guard |
| `crates/tb-server/src/replication_server.rs` | `4b6641546346578c354f7d7636fda35d22c82163fe01e522317f42cd8df62e80` | current durable-only `decode_and_append` |
| `crates/tb-server/Cargo.toml` | `7a5fc58425699e9c42eb6b89a0036ee02f0feaf6468487ccdb13857f9316a3fe` | `tb-document` is dev-only |

`CertifiedReplayEvent` already exposes `event()`, `source_changes()` and its
exact `OwnedDurableGroup`. Live `CertifiedEvent` exposes only `id()` and
`digest()`, although it retains the same verified proof. A narrow
**read-only** live `event()`/`source_changes()` accessor in
`tb-replication/src/authority.rs` is prerequisite; it must not expose a
constructor, mutable proof, or an unverified source-byte route. The receiver
then copies the proof's exact full D1 key/value bytes. Decoded
`DocumentValue`, sender D0/D6, D3 retry, and physical indexes are never
remote merge input. Record formats 1 and 2 remain accepted; a valid v1
inline-state sender is translated to local D0 v2/D6.

## Inputs, causal applicability and duplicate policy

The live document entry accepts one `CertifiedEvent` and the matching mutable
`ReplicaReceiver` for its authenticated origin inside one synchronous caller.
The replay entry accepts one `CertifiedReplayEvent` produced from
`ReplicaReceiver::read_durable_exact(sequence, max_bytes)`, a fresh
`verify_incoming` proof, and `certify_durable_replay` against the persisted
signed authority. Keep the owned exact bytes, semantic work permit and
authority view alive until target preparation/publication completes. Replay
never calls `certify_peer`, `append_certified`, or a live ACK constructor.
Both entries bind complete EventId, digest, epoch, configuration, database,
shard, origin, logical change ordinal, signed schema generation/digest and
exact D1 bytes before touching target state. The accepted certificate fixes
the first-slice membership; no epoch transition is inferred here.

Under the existing exclusive document writer gate, derive each remote origin's
contiguous applied prefix from recovered D5, and the local source prefix from
audited D4. D4 is usable only for the certified local source origin: the
retained source-frame audit must establish every sequence through that D4 and
its published D0→D6→D1 state. It cannot stand in for a remote D5. For an
incoming remote origin, require `sequence == D5_prefix+1`
before applying. `sequence <= prefix` is a duplicate only if its D5 identity
matches EventId/digest/epoch/configuration **and** the corresponding exact
durable inbox group is present and matches; a differing identity or missing
marker is corruption. For live duplicates, first require
`read_durable_exact` to find that marker, then let the existing journal
duplicate path compare the full canonical bytes. A new event above the next
sequence, or one whose observed cross-origin prefixes are not yet locally
applied (D5 for remote origins, D4 for the local source), may be retained in
the bounded inbox as durable-only pending work. It publishes no D5 or D0,
advances no applied prefix, and requests repair/replay when predecessors
arrive. An unavailable dependency is not silently treated as an empty state.
Reject an observed origin absent from the fixed signed membership, rather
than leaving an unfulfillable dependency pending forever.
The verified event's own observed origin prefix must equal `sequence-1` as
enforced by `CommitEvent::new`; no timeout licenses skipping it. A duplicate
with `AlreadyCommitted` from raw storage is resolved from D5 plus the exact
inbox marker, never from the sender transaction token or a shard commit
sequence.

For each changed document, read the current D0 root and D6 manifest at one
snapshot, verifying every followed CRC, hash, identity, signed schema and
D1 descriptor. Compute the causal join on **descriptors**: context is the
componentwise maximum of old context, the change's observed context, and
the new `(origin, sequence)` dot. Retain an old sibling only when its dot is
not covered by the change's observed prefix; add the new descriptor in
canonical EventId order. An existing same D1 key with exact bytes is reusable;
different bytes at that key or at a content-addressed D6 key are corruption.
Enforce the effective 64-origin/64-sibling first-slice cap, complete event
payload and per-value limits, and explicit capacity/backpressure; never trim
causal evidence, choose a conflict winner, or clone all old sibling bodies.
The source writer must use this same descriptor merge semantics when later
writing over remotely applied state.

## Bounded staging and atomic publication

The final target group is **exactly** copied D1 + new D0(v2) + one D5, with
no D2/D3/D4 or receiver indexes. D5 is `TBAPL001` for the certified complete
EventId/digest/epoch/configuration, independent of sender retry tokens. With
`n` changes and exact total D1 value bytes `X`, its WAL length is
`267 + X + 208*n` and its operation count is `2*n+1`; the accepted source
frame inequalities cover both valid v1 and v2 groups. This proof does not
waive memory, disk, checkpoint, schema or ENOSPC admission. D1/D0/D5 must
appear in one synced target WAL frame; never publish D5 separately.

Pass 1 walks changed documents under the writer gate, computes D1
descriptors and D6 hashes, compares any existing D6 exact bytes, and packs
only absent D6 Puts into bounded **pure-D6** groups. Keep one stage group
and compact fingerprints/footprints rather than all manifests or sibling
payloads. Preflight actual WAL bytes, operation count, mutable memory/items,
disk quota, run slots and checkpoint scratch. Build exact `PlannedBatch`
stage digests and an exact or checked upper bound for the final group; call
`reserve_stage_work(StageWorkPlan)` for **all** stages and final publication
before the first stage commit. Pass 2 regenerates one D6 group at a time,
checks its fingerprint/exact plan, and commits it with fresh snapshots.
Reopen/retry compares and reuses already synced D6 values; orphan manifests
remain inert and disk-accounted, with no GC in this slice. A refused plan
writes nothing.

After staging, take the post-stage snapshot and freeze the final D1/D0/D5
mutations. Use a receiver-local fresh final transaction token and distinct
stage tokens with checked expiry; D5, not these tokens, resolves replay after
expiry or restart. Call `StageWorkReservation::prepare_final` (or the same
prepared storage seam for a zero-stage plan) **before** live inbox marker
sync. `Ready` owns the target storage-inner guard and admitted exact frame.
Live order is document writer → document storage-inner → inbox
receiver/journal: consume the `CertifiedEvent` with
`ReplicaReceiver::append_certified`, require a synced contiguous durable
prefix covering this event, then commit `Ready`. No path takes the reverse
order. Replay uses the already synced exact group and commits the prepared
target without a second inbox append. All fsync and held-guard work runs in
one bounded blocking task; no guard crosses `await`. This is a document
engine result only; it does not issue a network `AppliedAck`.

Definite pre-stage admission failure changes neither shard. A failure after
a D6 stage but before target final leaves only reusable, unreferenced D6.
If inbox append fails or may have synced a marker, drop the uncommitted
prepared target, close applied readiness, and reopen/reconcile both shards
before another applied attempt. If the inbox marker is durable but D5 final
is absent, replay that exact group. If final commit fails or its outcome is
uncertain, poison the document writer and require reopen; never answer
applied from an in-memory receipt. If D5 persisted, the exact-match path
skips reapply. A final-token expiry, physical ENOSPC, capacity failure,
corruption, marker collision or D5 mismatch yields no applied claim. Keep
the blocking task running to resolution after async caller cancellation;
recover a lost response from durable D5 and visible state.

## Reopen, resend and server handoff

Packet A's `distributed_state::audit_retained_frames` and visible-key scan
currently reject foreign D1, all D5 and any final frame without D2;
`distributed_producer::next_source_after` likewise errors on a receiver
final frame. Extend both narrow paths to classify four allowed WAL groups:
signed C0 install, pure-D6 stage, complete local D1+D0+D2+D3+D4 source
event, and complete receiver D1+D0+D5 final group. Audit every receiver
group's exact role/count, D5 identity/scope/order, D1 immutable bytes and
D0→D6→D1 chain. The document-only open may prove structural D5 validity,
but **cannot** assert inbox coupling or applied readiness without opening
the sibling journal shards. Preserve local source high-water auditing and
have resend skip receiver finals as well as pure-D6 groups. It emits only
complete local D2-backed events, in origin sequence order, and never emits
D5 or a receiver final group. Current local-v1 `DocumentStore` semantics
stay separate.

On startup the later runtime opens the accepted authority, the
authority-bound `documents/` shard and each relevant ordinary raw
`inbox/<origin>` shard. Under the same lock order it compares contiguous
D5 with each receiver's durable prefix, reads exact groups from
`D5_prefix+1`, re-verifies and replay-certifies them, then applies or
quarantines. It must reconcile preexisting durable-only backlog and deferred
gaps before serving applied-capable writers. A D5 ahead of a durable marker,
a gap in D5, changed exact bytes, or a broken visible D0/D6/D1 chain closes
readiness. Durable inbox prefix and transient `OriginCursor::applied` are
not applied authority. Prometheus-compatible metrics may expose bounded
stage reuse/orphan bytes, pending/replay counts, D5 prefix, capacity/corrupt
failures and ACK stage without per-event labels.

The server currently holds `OriginInbox.receiver` around
`decode_and_append`, and its `frontier_for` sets `applied_sequence: None`.
`tb-document` is only a dev-dependency of `tb-server`. Server ownership
must add the production dependency, authority-bound document bootstrap and
one bounded blocking receive worker that composes the document writer,
storage guard and inbox journal without a guard across `await`. Its current
DurableAck remains valid for journal-only, deferred or not-yet-applied work.
An `AppliedAck` or populated applied frontier is a **later** gate requiring
recovered contiguous D5, completed backlog replay, a budgeted user-visible
read of the same D0→D6→D1 state, lifecycle/metric checks and real mTLS/LZ4
replica convergence. A document-unit return value or inbox marker alone
cannot satisfy that gate.

## Executable ownership and finite gates

1. **Narrow replication accessor:** the replication owner changes only
   `crates/tb-replication/src/authority.rs` and its tests to expose borrowed
   read-only event and exact source changes from live `CertifiedEvent`, as
   replay already does. Keep authority construction and replay/live types
   separate. Verify foreign-view/stale/altered proofs and v1/v2 exact D1.
2. **Receiver document packet:** after Packet-A source tests and snapshot
   handoff, the document owner adds
   `crates/tb-document/src/distributed_apply.rs`, updates
   `distributed.rs`, `distributed_state.rs`, `distributed_producer.rs` and
   `lib.rs`, and owns `crates/tb-document/tests/distributed_apply.rs` plus
   affected open/source tests. No default production edit to `tb-storage`,
   `tb-format`, or `tb-server`. Test two and three certified origins writing
   the same document concurrently, both arrival orders, retained siblings
   including tombstones, a later observed write resolving them, source
   resend after receiver finals, and restart/reconnect with different local
   WAL/index layouts. Test exact v1/v2 D1 copy, stale/unknown signed schema,
   64/64 and byte/item/disk refusal, duplicate exact versus collision,
   origin and causal dependency gaps, and D5 contiguous-prefix recovery.
   Fault cuts before/after each D6 sync, inbox chunk and marker sync, final
   WAL sync, and response loss must yield either no D5 with replay or one
   matching D5 and the same visible state; never D5 without inbox evidence.
3. **Real caller and ACK gate, separate ownership:** the server owner later
   changes `crates/tb-server/Cargo.toml`, `replication_server.rs`, `state.rs`
   and process tests. Verify actual two-replica mTLS/LZ4 send, durable-only
   gap/backlog, blocking-task cancellation, cross-origin replay, visible
   reads, Prometheus metrics and no lock inversion before any applied field
   is emitted. Keep benchmark artifacts/results in `../ts-benchmark`.

The bounded receiver packet is blocked on Packet-A runtime qualification,
the live certified proof accessor, and a reviewed effective 64/64 limit
handoff. It does not decide dynamic membership, tombstone reclamation,
receiver index writes, object durability or stronger acknowledgment policy.
Loopback delivery of the local source origin is outside this remote receiver
packet; the server gate must reject it or define an explicit D4/D5
reconciliation proof before enabling applied readiness for that path.
