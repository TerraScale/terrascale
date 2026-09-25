# Distributed document production, apply, and readiness integration brief

Status: **design / NOT IMPLEMENTED**. This is an executable follow-on to
[distributed-document-open.md](distributed-document-open.md),
[document-state-v2.md](document-state-v2.md), and
[replication-v2-replay.md](replication-v2-replay.md). The accepted physical
grammar, event verification, and durability order in those documents control.
The presence of mode/C0, storage reservation, exact-source, and replay APIs in
the working tree is not acceptance of this runtime. Source production is the
first packet; receiver apply, server wiring, and applied readiness are gated
on it and on the previously specified replay proof.

## Source identity and current seams

TerraBase HEAD is `352d9f61185d1b685e3032519579040d169ad47d`.
These SHA-256 values identify the **uncommitted working tree** inspected on
2026-09-24; refresh moving files before implementation or review.

| Path | SHA-256 | Evidence |
| --- | --- | --- |
| `docs/document-state-v2.md` | `22ef94c55efa856422b8ec033192ccbd68c8cc122c20447e6878dc4ad4965992` | D0/D5/D6 grammar, frame arithmetic, publication order |
| `docs/replication-v2-replay.md` | `c87b9f6634786df1bfb56edac0b473bd0cd4c508bd45ccf01341cff29441cf49` | exact replay and format proof packet |
| `docs/distributed-document-open.md` | `23a77e39d11eab93ef9f07c3210db18c6d55ca206822bbeb7862dea7aaca59ac` | authority-bound mode/C0 boundary |
| `crates/tb-document/src/distributed.rs` | `f1607666a2b1550e25e84c4dc5a5989a4d30067b0bc24ad278be6746f22ed910` | `open_distributed`, `audit_exact_catalog` |
| `crates/tb-document/src/producer.rs` | `dc93b4982922afded4ff5e257bb07edb3c9308dabbba77e937cad1e9179dab02` | local-v1 `commit_documents`, `reconcile_local_tail`, `resolve_retry` |
| `crates/tb-storage/src/local.rs` | `22d5ec284fa8c1d579ca3185e268ccd11556dd39a2beb0297f6cc57e99d33e73` | `PlannedBatch`, `reserve_stage_work`, `StageWorkReservation`, `prepare_batch` |
| `crates/tb-replication/src/metadata.rs` | `2c8a8b6d9d9d8921cb386b99a10f1962cc31ac7bf95badc56640855c6630e8e3` | `VerifiedIncomingGroup::source_changes` borrows exact D1 bytes |
| `crates/tb-replication/src/authority.rs` | `867eb8293903c7fa622928305e3b4c40aa0dbe30f98857b15a9194df6db3c0ed` | `read_durable_exact`, `certify_durable_replay`; certificate admits at most 64 members |
| `crates/tb-replication/src/journal.rs` | `d23b140fbfedad340afaf6a8519fda50b8070176d354a29cb3bea5ee95c530a0` | inbox open remains ordinary raw storage |
| `crates/tb-format/src/document_v2.rs` | `da73c94766d11f4b0be7194a6535c72e9bf8d5baef5ddb24af45388cfb30f7da` | D0/D5/D6 codecs and size constants |
| `crates/tb-server/src/replication_server.rs` | `4b6641546346578c354f7d7636fda35d22c82163fe01e522317f42cd8df62e80` | `decode_and_append` holds inbox receiver mutex, emits durable ACK only |
| `crates/tb-server/src/state.rs` | `0735ff2833aa7049fcc29b56adb1e640651c751c75d2c167f9e1074c7f453227` | `LocalServerState` still opens separate raw `--data-dir` shard |
| `crates/tb-server/Cargo.toml` | `7a5fc58425699e9c42eb6b89a0036ee02f0feaf6468487ccdb13857f9316a3fe` | `tb-document` is only a dev-dependency |

`DistributedDocumentStore` currently retains certified authority and storage
but exposes no document write or read. Its full visible-key audit rejects all
non-C0 rows, so the source packet must replace that catalog-only audit before
it can publish D1/D2/D3/D4/D6; receiver apply later adds D5. The local
`DocumentStore` remains the separate v1 caller. The storage stage interface
is concrete but not yet qualified through a document caller: it accepts exact
stage digests and a final exact or upper-bound footprint, reserves the total
planned disk/memory/item/run work, and checks each regenerated stage before
commit. `prepare_final` returns `Ready` with the storage-inner guard or
`AlreadyCommitted`; it does not itself sync an inbox. Do not infer applied
readiness from any of these APIs.

## Packet A: certified distributed source writer and two-pass D6 staging

**Write ownership:** `crates/tb-document/src/distributed.rs` for the handle,
writer gate and open audit; proposed new
`crates/tb-document/src/distributed_producer.rs` for source retry, final event
and resend; proposed new `crates/tb-document/src/distributed_state.rs` for
descriptor merge and D6 stage packing; `crates/tb-document/src/lib.rs` for
module declarations and public exports; and
`crates/tb-document/tests/{distributed_open,distributed_source}.rs` for the
real document caller. The storage owner owns the independent 11+1 physical
boundary test in `crates/tb-storage/tests/staged_batch_contract.rs`. Touch
`crates/tb-storage/src/local.rs` only for a demonstrated seam defect, after
the storage owner accepts a separate bounded change; preserve raw-key callers.
No change to the accepted open brief, local-v1 producer behavior, or wire
grammar. The document owner chooses a focused public writer method on
`DistributedDocumentStore` returning the same exact metadata/frame-bearing
`DocumentCommit` contract used by resend, with no sender-local D6 in the
event. The handle retains the accepted authority and a single exclusive
document writer gate covering source writes, eventual remote apply, catalog
changes, and recovery. Every write validates its collection against the
signed **active C0** from `CertifiedShardView::checked_active_catalog`; caller
binding and C1 are never schema authority.

Under the writer gate, resolve a caller retry from D3 and its retained exact
source frame **before** planning new stages. For a new event, freeze origin
sequence, logical sorted change order, causal observations, source D1 bytes,
schema bindings, and intended final transaction identity. Build D6 descriptors
from D1 value length and SHA-256 without copying old sibling bodies. Follow
existing D0→D6→D1 references at one snapshot and verify hash, CRC, identity,
schema, and causal limits. Use checked arithmetic and budgeted page/work
permits; reject capacity before publication when one legal next item cannot
fit. A hash-keyed D6 already visible at the same snapshot is reused only
after exact byte comparison; mismatch is corruption.

Pass 1 regenerates candidate D6 values one document at a time and greedily
packs **absent** D6 Puts into bounded pure-D6 WAL groups, checking actual
encoded frame bytes, operation count, mutable bytes/items, directory bytes,
disk envelope, and checkpoint headroom. Keep at most one bounded stage group
and compact per-group fingerprints/footprints; never retain all manifests or
all old sibling payloads. Produce an ordered `PlannedBatch::exact` for each
group. Build the source final batch bound (or exact plan if already frozen):
D1+D0(v2)+D2+D3+D4, `record_format=2`, event format/merge policy 1. Its frame
must fit both 2 MiB and 1,024 operations, including all metadata and optional
source indexes if enabled. Call `Storage::reserve_stage_work` with the full
`StageWorkPlan` **before** the first D6 commit. A refused plan writes nothing.

Pass 2 regenerates each stage in the same order from the gated logical input,
checks its exact plan digest and footprint, then calls `commit_stage` using a
fresh storage snapshot. Stage transaction IDs are distinct from one another
and from the caller's final retry token; a retry after a crash reopens,
compares already staged D6 bytes, and makes a fresh plan for only missing
values. Stage groups contain no D0, D1, D2, D3, D4, or D5. After all stages,
take the post-stage snapshot, freeze the exact final bytes, and consume
`prepare_final`/`Ready` to publish one atomic source group. A mismatched final
bound, uncertain sync, or post-sync verification failure closes the writer
until reopen. A successful return includes the exact retained committed WAL
frame and digest verified against its D2 marker; no source success or resend
cursor is advanced by a D6-only stage.

The codec's maximum D6 value is `52 + 40*1024 + 143*1024 = 187,444` bytes;
its key is 66 bytes. A Put adds 9 bytes and a frame adds 60:
`11*(187444+66+9)+60 = 2,062,769` fits 2,097,152 bytes; twelve total
2,250,288 does not. This is a **physical packer** bound, not a certified
producer scenario. `AuthorityCertificate::encode` and certificate parsing
admit only 2–64 members, and the current server `EVENT_LIMITS` sets both
`max_origins` and `max_siblings` to 64. At those runtime limits, even a
maximally populated manifest is only `52 + 40*64 + 143*64 = 11,764` bytes;
twelve such Puts take just `12*(11764+66+9)+60 = 142,128` bytes. The physical
2-MiB edge for that maximum is 177 Puts at 2,095,563 bytes; 178 take
2,107,402 bytes, subject also to other admission limits. The current
document config allows independently chosen replication limits, so Packet A
must enforce the effective receiver-compatible limits at production rather
than emit a source state the real server rejects. Test 11+1 splitting with a
real storage stage caller and twelve valid maximum codec values. Before
receiver apply exists, an organic Packet-A source can write only its own
origin: the local producer adds its previous sequence to `observed`, so its
old same-origin sibling is removed. With one context and one sibling, D6 is
`52+40+143 = 235` bytes and a Put is 310 bytes; even 510 changed documents
would consume only `510*310+60 = 158,160` stage-frame bytes. Test the
certified Packet-A source caller at this reachable state through the **same**
packer, including D6 reuse and exact final publication. A multi-origin
source-conflict scenario belongs after Packet B's certified apply path is
available. Do not enlarge authority membership solely to manufacture a test
case.

Extend distributed reopen audit to classify every retained WAL group and
visible record: signed C0 installation, complete verified source-event groups,
and pure-D6 staging groups only in this packet. Reject unknown raw writes,
Deletes, mixed stage/event frames, unsigned C0, C1, D5, and unproved indexes.
Audit all D2/D3/D4 identities and each visible D0→D6→D1 chain, not just the
latest high-water frame. Unreferenced D6 from a crash is inert, reusable by
exact content, charged to disk/orphan metrics, and retained without GC in this
slice. Resend enumeration scans retained committed groups, skips proven C0
and pure-D6 groups, and emits only complete D2-backed source frames in
per-origin sequence order. It must not assume physical WAL sequence equals
event sequence or that the most recent physical frame is an event.

**Packet-A gate:** the storage-owned test covers the 11+1 maximum-codec
stage split, exact regeneration mismatch, and retry between its two stages.
Document-owned tests cover signed active C0 versus forged binding/C1, normal
v2 source verification and unchanged local-v1 fixtures, and the reachable
certified source case through the same packer. Also prove total reservation
refusal before any D6 write; D6 reuse and collision; caller retry after its
stage and after final sync; D0 never points to a missing D6; all-frame reopen
and resend filtering after interleaved stage/event frames; near-1-MiB D1;
count/byte/item/disk limits; and budgeted peak memory.
Only the document source caller, not a storage unit test alone, qualifies
source production. Benchmark files and results belong in `../ts-benchmark`.

**Packet-A prerequisites before scheduling:** handoff of the in-flight
snapshot-isolation/storage work and revalidation of `StageWorkReservation`
semantics on that source revision; qualification of the mode/C0 open and
signed-active-catalog path through its real caller; v2 D0/D6 codecs and
record-format-2 same-frame verifier with exact D1 extraction; an agreed
effective producer/receiver `ReplicationLimits` contract (64/64 in the
current server); and document-test access to an accepted v2 authority and
retained WAL cursor. Treat present code as a seam, not as a passed gate. No
production edits to storage, format, replication, or server are in Packet A's
default write ownership.

## Packet B: certified receiver apply and exact replay

**Dependencies:** Packet A's shared document writer gate, D0/D6 merger and
audit; verified exact D1 source views; storage `prepare_final`; durable exact
read and replay certification. **Write ownership:** a separate receiver/apply
module in `crates/tb-document/src/` and document tests; any missing narrow
replication replay API belongs to `crates/tb-replication/src/` and its owner.
The receiver input is a live `CertifiedEvent` or replay-only
`CertifiedReplayEvent` plus its proof-owned `VerifiedSourceChange` slices.
The document module must not independently certify raw bytes, copy sender D0,
follow sender D6, use sender D3, or accept physical indexes as merge authority.
It copies exact D1 key/value bytes from the verified source frame. A valid v1
inline-state source is translated into local v2 D0/D6 state.

Under the same document writer gate, require contiguous per-origin
applicability; check existing D5 by complete EventId/digest/epoch/configuration
for idempotent replay, and reject a collision or gap. Read the local D0 state
at one snapshot and merge causal descriptors/contexts without materializing
every sibling body. Plan and stage absent D6 as in Packet A. Freeze the final
receiver batch as **exactly D1+D0(v2)+D5** and call
`StageWorkReservation::prepare_final` (or `Storage::prepare_batch` when there
are no stages) before inbox marker sync. `Ready` holds the document
storage-inner guard while the exact certified group is synced to its origin
inbox; then consume `Ready` to commit the final document group. Global lock
order is document writer → document storage-inner → inbox receiver/journal.
No path, including recovery, takes the reverse order. `AlreadyCommitted`
requires a matching recovered D5 and durable inbox identity before reporting
an applied result; it is not a shortcut around certification. D5 is the replay
identity and the only persisted applied-prefix evidence. On a definite
prepublication failure, any staged D6 remains inert. An uncertain or post-sync
failure forces reopen/replay and no applied claim.

Replay scans each origin's **contiguous durable** inbox prefix with
`ReplicaReceiver::read_durable_exact`, reruns full metadata/WAL/D1 verification,
then calls `certify_durable_replay` against persisted signed authority. It
reconciles recovered D5 before admitting new writers. A matching D5 skips
publication; missing D5 replays the exact group; mismatched D5 or a broken
D0→D6→D1 chain quarantines readiness. A missing durable marker inside a
reported prefix is corruption. The inbox journal stays an ordinary raw shard
under its own sibling path; only `documents/` uses distributed document mode.
No deletion of D1/D5/D6 or old D0 versions is in this packet.

**Packet-B gate:** finite crash cuts before/after D6 sync, inbox chunk sync,
inbox marker sync, and document final WAL sync; forced reopen at each cut;
duplicate, collision, gap, expired token and stale schema; old durable-only
backlog; valid v1 source translation; exact D1 byte equality in target final
frame; and corruption of every followed D0/D6/D1 link. Assert no target D5
without a durable inbox marker, no applied prefix ahead of contiguous D5,
and no document pointer to an absent manifest after every recovery.

## Packet C: real server caller, visible reads, and applied gates

**Dependencies:** A and B pass. **Write ownership:** `crates/tb-server/src/`
and `crates/tb-server/Cargo.toml` for the runtime dependency and composition;
`crates/tb-document/src/` owns the read API and document snapshot/lease work.
Open the authority-bound `documents/` shard alongside the existing separate
raw `--data-dir` state, retain one shared document handle and accepted
authority, and connect the actual gRPC receive path. Today
`decode_and_append` holds `OriginInbox.receiver` around `append_certified` and
returns only durable ACKs (`applied_sequence: None`). The applied path must
perform document prepare, inbox sync, and final commit in **one bounded
blocking task**, keeping its serial admission and lock order as above; no
synchronous guard crosses `await`. Cancellation after that task starts leaves
it to resolve sync/commit; a lost response is resolved from durable D5 on
reopen. Preserve mTLS member certification, gRPC/LZ4 transport limits and
durable ACK behavior until the applied gate is proved.

Expose a budgeted visible read using one pinned snapshot for C0/D0→D6→D1.
Check every CRC, hash, identity and schema binding; return an explicit stale
continuation or capacity error rather than silently dropping a sibling. Any
future reclamation needs snapshot/reader/backup pins first. On startup,
reconcile every existing durable inbox and D5 prefix, verify a user-visible
read of the recovered state, and only then open applied readiness or populate
`applied_sequence`/`AppliedAck`. Readiness closes on quarantine, capacity
failure requiring operator action, uncertain durability, or replay lag that
prevents the promised applied service. Prometheus-compatible metrics expose
D6 staged/reused/orphan bytes, replay progress/failure, D5 applied prefix,
capacity refusal and durable versus applied ACK counts with bounded labels.

**Packet-C gate:** one real sender and receiver over authenticated gRPC/LZ4,
different physical index layouts, v1 and v2 source frames, backlog and crash
replay, and a visible read matching the recovered D5 before an AppliedAck.
Check lifecycle readiness, cancellation, worker bounds, metrics and no
receiver/document lock inversion. Keep applied fields absent until this gate
passes; a recommendation or successful unit test is not runtime acceptance.

## Open decisions and limits

The first slice explicitly rejects otherwise valid writes when 1,024 causal
origins/siblings, memory, disk, or event limits are exhausted. Orphan D6
retention and missing reclamation pins prohibit safe GC now. A stronger
membership scenario than the current 64-member certificate, index writes in
the receiver final frame, retention compaction, and stronger durability or
multi-region ACK policy require separate product/architecture decisions and
their own frame, recovery and cost proofs. None is silently enabled by these
packets.
