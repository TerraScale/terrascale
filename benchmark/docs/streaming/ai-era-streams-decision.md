# GAP-C3 phase 1 — AI-era streams decision record (competitive matrix row CP-109)

Status: **PROPOSED decision record, awaiting parent ratification.** This is
the phase-1 deliverable of ticket TerraScale/TerraBaseV2#85 (GAP-C3), the
evaluation ticket the competitive matrix names for row CP-109
(`docs/competitive/matrix.md:66`, filed from
`docs/competitive/gap-ticket-proposals.md:79-109` per PD-2026-08-26-06 D4's
"AI-era streams — evaluation row"). The packet's dispatch recorded no ticket
body beyond the filed proposal text, so this record evaluates exactly the
five axes that proposal names. Documentation only: no engine code, no parity
overlay, no competitive-matrix status change, and no Forgejo object was
created or edited by this packet. Every decision below is PROPOSED — phase 2
must not start from this record until the parent ratifies or amends it.

Worktree `.worktrees/terrabasev2-gapc3`, branch `fleet/gapc3-2`, based on
`develop` @ `da0df43`. V1 citations resolve against the pinned read-only
checkout `fa99b9257776dbe625e6a2388c74c9ce916637ee`; each was re-resolved
there for this record.

## What this record decides, and what it does not

It decides (each PROPOSED, each with a named rejected alternative):

- **D1** stream identity and metadata surface — who mints the identity, where
  metadata lives, how it is queried and linked to entities.
- **D2** persistence substrate — storage family records vs journal windows vs
  the digest-addressed content plane.
- **D3** resume semantics — byte-offset vs typed checkpoint.
- **D4** authorization model — engine rules vs backend-supplied tenant
  context.
- **D5** first-class stream object vs composition from existing seams.

It does not decide implementation, bounds, frame vocabulary, or any code
location finally — those are phase-2 inputs sketched at the end. It flips no
parity row and no matrix row: CP-109 stays `engine gap (evaluation first)`
until the parent records a disposition on ratified decisions, and no V1
parity row exists for this capability (section "Why no V1 parity row is
opened").

Vocabulary follows `CONTEXT.md`. Because "stream" is already overloaded (V1's
document streaming rows PB-6000/PB-6007..6009; `src/streaming/` is the
document- and change-delivery seam; a Live View must *avoid* the word), this
record says **AI-era stream** for the CP-109 object and means exactly: an
engine-owned, durable, append-only, byte-addressable buffer with one
serialized writer, queryable progress metadata, and resumable reads — the
object InstantDB ships and markets for LLM output.

## Inputs

Charter sub-decisions are cited as `PD-NN D#`; a bare **D1..D5** always means
*this record's* decisions.

- **PD-2026-08-26-06** (competitive objective): PD-06 D1 — parity plus
  superior evidence, never asserted superiority; PD-06 D2 — every row cites
  competitor evidence and carries a disposition; PD-06 D4 — AI-era streams is
  an evaluation row; PD-06 D3 — engine rows (streams named) route to engine
  waves, the engine grows no functions runtime.
- **PD-2026-08-26-07** (AI-native): PD-07 D1 — agents reach the capability
  through typed, MCP-exposable operations under the same rules as humans, no
  separate agent API; PD-07 D2 — "event streams and subscriptions for agents"
  is a named agent-native engine capability.
- **PD-2026-08-26-11 D1** (local backend-only surface): no authentication or
  authorization surface at the engine/host boundary; customer auth stays at
  the Platform; **tenant scoping stays engine-enforced from backend-supplied
  tenant context**.
- **PD-2026-08-26-13** (bandwidth, trigger events): PD-13 D1 — measured
  bytes-on-wire budgets for every link the sync engine feeds; PD-13 D3 —
  commit publication emits small, typed, **content-free** Change
  Notifications; push the hint, pull the delta.
- **PD-2026-08-26-03**: any latency/throughput/bandwidth statement requires a
  Benchmark Receipt in the exclusive lane; verdict vocabulary used exactly.
- Competitor evidence: **EV-I10** (InstantDB streams) and **EV-C1**
  (Convex docs index) from the landed matrix's evidence files
  (`docs/competitive/evidence/instantdb-survey-2026-08-26.md:165-184`,
  `docs/competitive/evidence/convex-survey-2026-08-26.md:235-238`).
- Landed seams this evaluation measured against: the SE-03 change-event
  backbone (`docs/streaming/se03-change-backbone.md`,
  `src/streaming/backbone.rs`), the storage-family substrate
  (`src/families/state.rs`), the attachment content plane
  (`docs/families/attachment-content-plane.md`, GAP-F01 #71), and the
  subscription seam (`src/streaming/subscription.rs`).

## The evaluated capability, from the evidence

| EV-I10 observable (InstantDB) | Evidence line | Decision it drives |
| --- | --- | --- |
| Server-assigned stream identity (UUID); one writer per `clientId` | EV-I10 "one writer per `clientId`; server-assigned stream UUID" | D1, D5 |
| Written data "buffered in memory on Instant's servers", periodically flushed to Storage, "fully flushed ... when finished" | EV-I10 verbatim | D2 (better-than measure) |
| Streams "never expire" | EV-I10 (via `@instantdb/resumable-stream`) | D2 (retention) |
| Readers "can pick up from any point in the stream, and resume if the connection is lost" (`createReadStream` with `byteOffset`) | EV-I10 | D3 |
| Metadata (`done`, `size`, `abortReason`) queryable via the `$streams` namespace and linkable to entities | EV-I10 | D1 |
| Permission rules default to `"false"` on `$streams` | EV-I10 | D4 |
| Marketed "excellent for LLM-native applications"; `consumeSseStream` resumes LLM SSE output server-side; `reconnectToStream` resumes from the browser | EV-I10 | D5 (delivery shape), phase-2 frames |

Convex, for matrix honesty, ships no verified equivalent: "Streaming" is a
docs-index listing under AI Agents (EV-C1, page not fetched) and "no
storage-backed resumable-stream primitive is documented on fetched pages"
(`docs/competitive/evidence/convex-survey-2026-08-26.md:235-238`). So the
contract to match is InstantDB's, and the better-than measure is stated
against InstantDB only.

## D1 — Stream identity and metadata surface (PROPOSED)

**Chosen.** An AI-era stream is identified by an **engine-assigned, opaque,
typed stream identity minted in the creating transaction** from the identity
family, and its metadata is a **reserved storage-family record** under a kind
key of its own (`family:streams/<stream-id>`), carrying at minimum: status
(`open` / `sealed` / `aborted` — InstantDB's `done` and `abortReason` made a
closed enum), the cumulative sealed byte `size`, the per-segment byte lengths,
the recorded writer identity while open, and caller-supplied **entity links**
(collection + document identity pairs). The metadata surface is served by
**typed operations** (list/query by link and status, read one stream's
metadata), which makes it MCP-exposable under PD-2026-08-26-07 D1 without a
separate agent path.

**Why.** Every element is the engine's existing discipline applied once more:
identity allocation inside the creating transaction is the already-adjudicated
no-explicit-ID rule (uniqueness is a database invariant, so a crashed create
leaks no ids — the GAP-D01 record's row 12 (docs/sessions/gapd01-ownership-decision.md:65); `identities_read`/
`identities_stage` at `src/families/store.rs:670,686`); reserved records under a
distinct kind key are exactly how family values live today
(`src/families/state.rs:32` — one reserved collection `terrabase.families`;
`src/families/state.rs:93` — the kind-key map); and a kind key that diverges
from user text before any owner component is the collision lesson the
attachment content plane learned empirically and repaired
(`docs/families/attachment-content-plane.md` D1 and the warden repair: a
metadata document whose name collided with a segment key was silently
destroyed). The `$streams` precedent (EV-I10) proves a queryable metadata
namespace is part of the competitor contract, not an addition.

**Rejected — client-supplied stream identities.** A caller-chosen id makes
uniqueness a caller obligation the engine cannot enforce across tenants, and
the engine already owns the machinery for exactly this. **Rejected — metadata
as an ordinary user document.** Then `done`/`size`/`abortReason` are
unenforced user data: nothing stops a `size` that disagrees with the bytes,
and the single-writer and status transitions have no authoritative home.
**Rejected — a second reserved collection** (`terrabase.streams`) beside
`terrabase.families`: it forks reserved-record classification, backup
enumeration, and family replication for one new kind, instead of extending
the closed kind set (see the phase-2 shape for that shared-file consequence).

**Recorded limit.** InstantDB's `$streams` is queryable *in its query
language*; V2's reserved records are readable through typed family reads
today, and whether TQL may address reserved records is not decided by any
landed ticket. Typed-operation queryability matches the matrix's own required
proof wording ("metadata queryable as typed operations",
`docs/competitive/gap-ticket-proposals.md:104-105`); TQL surface over reserved
records is deferred to the TQL wave with a named owner (below).

## D2 — Persistence substrate (PROPOSED)

**Chosen.** Stream bytes live as **reserved storage-family records** — bounded
segment records under the stream's kind key, reusing the content plane's
*encoding* but not its *addressing*: `family:streams/<stream-id>/<segment-
index>`, each segment at most the segment byte bound (the content plane's
`MAX_SEGMENT_BYTES` is 64 KiB), appended and read through ordinary engine
transactions and immutable snapshots. Appends extend the open **tail segment
in place** until it reaches the segment bound, then seal it and start the
next; the metadata record's per-segment lengths list is updated in the same
transaction as the bytes it describes. Retention is **by explicit typed
deletion only** — no expiry, no compaction-driven loss — matching EV-I10's
"never expire" and exceeding the journal's own behavior (below).

**Why.** This is the only substrate in the tree that already carries every
obligation the row demands: tenant isolation and durability inherited from
the family seam with no new storage format (the content plane's argument,
`docs/families/attachment-content-plane.md` D1); atomic staging with ordinary
documents in one `Engine` transaction; replication and backup of family
records as family records; and bounded reads by construction (a reader
decodes at most one segment at a time, so content of any accepted size is
never materialized whole).

**Rejected — journal windows** (appending stream bytes as publication-journal
entries and replaying windows). Two independent reasons, each verifiable in
the tree:

1. *The journal is a bounded, truncatable window, not a retention contract.*
   Journal and history are truncated at open and on compaction
   (`src/storage/file.rs:137-158`), and the engine itself exposes the
   consequence: `servable_history_start` returns the oldest publication the
   journal can still serve (`src/storage/file.rs:850`), and SE-03's replay
   contract is honest about it — a `None` anchor names "the servable
   beginning — the oldest publication history the journal can still serve"
   (`src/streaming/backbone.rs:118`). A
   stream whose bytes rode the journal would inherit an unobservable expiry
   cliff driven by engine-internal compaction policy — the exact opposite of
   the "never expire" contract, and it would silently break resume rather
   than fail closed.
2. *The journal is an event log of commits, not a random-access byte-range
   read surface.* Serving `read from byte N` would require new machinery to
   reconstruct byte offsets from journal records, while reserved segment
   records with a lengths manifest already give that addressability for free.

**Rejected — the digest-addressed content plane as-is.** The landed plane is
digest-addressed, immutable, and deduplicated by content hash
(`family:attachment-content/<hex-sha-256>[/<index>]`,
`src/families/state.rs:38-44`): the key *is* a function of the bytes. A
stream is append-mutated, so its content hash changes with every append and
no stable digest identity exists; forcing one would re-key every record per
append. The correct reuse is the bounded segment encoding and the
manifest-of-lengths shape, keyed by stream identity and monotonic segment
index instead of content digest. Recording this distinction explicitly is the
point of this decision: "reuse the content plane" said loosely would import
the wrong addressing.

**Why one kind key is safe here when it was not safe for attachments.** The
content plane's record-id collision existed because attachment *metadata* is
keyed by caller-supplied document text, so a document named
`content/<digest>/<index>` could land on a segment record's identifier. Here
the owner component is an engine-minted opaque identity (D1), so no
caller-chosen text can address the segment space — the collision surface is
closed by construction rather than by convention. Phase 2 still owes the
defense-in-depth rule the plane adopted after its repair: read and decode-guard
every segment identifier before writing or clearing it, failing closed on
foreign values.

**Recorded obligation, not a silent inheritance.** The content plane's
`MAX_CONTENT_BYTES` (8 MiB) / `MAX_SEGMENT_COUNT` (128) are the *attachment*
ceiling, recorded there as an accepted V1 difference. An AI-era stream must
set its own typed bounds with typed over-limit refusals; inheriting 8 MiB
silently would cap LLM output streams below the competitor's unbounded
contract without a recorded reason. Phase 2 owns those numbers (sketch
below).

## D3 — Resume semantics (PROPOSED)

**Chosen.** The **durable resumption identity is a typed, validated
checkpoint**, not a bare byte offset: an opaque `StreamCheckpoint` naming the
stream identity/epoch it was taken from, the segment index, and the byte
offset within that segment. **Byte offsets remain the caller-facing
addressing unit**: a read may start from any byte offset, resolved through
the metadata record's per-segment lengths (a prefix-sum over a bounded list),
and every read returns the checkpoint that resumes exactly after the bytes
served. Resume validates the checkpoint against the stream's current
metadata and fails closed on a different stream identity, a sealed-and-
truncated epoch, or an offset past the recorded size — it never silently
aliases onto the wrong bytes.

**Why.** This is the resumption discipline V2 has already committed to twice,
made byte-addressable. `ChangeCheckpoint` is "an exclusive journal cursor ...
a checkpoint carries no engine binding: it is the caller's position, and
replay validates it against the scope it is replayed under"
(`src/streaming/backbone.rs:88-96`); the subscription path binds every read
to the scope's validated tenant before the journal is paged
(`src/streaming/subscription.rs:498-504`); and `CONTEXT.md`'s Sync Checkpoint
entry exists precisely to keep "cursor"/"offset" from becoming the public
vocabulary of a durable resumption identity. V1's own subscriptions resume
the same way — by the change vector of the last acknowledged batch, not by a
positional integer
([v1:src/TerraBase.Client/Documents/Subscriptions/AbstractSubscriptionWorker.cs:764]).

**Rejected — a bare `u64` byte offset as the durable identity** (InstantDB's
`byteOffset` taken literally). A bare offset names no epoch of the stream: an
aborted-and-recreated stream, or one truncated after an abort, produces byte
positions that alias onto different bytes, and the caller cannot detect it.
It also cannot express "inside segment N" recovery or refuse a mid-code-point
resume position. The competitor contract's *observable* — "readers can pick
up from any point in the stream, and resume if the connection is lost"
(EV-I10) — is fully preserved by checkpoint-with-offset-addressing; what is
rejected is only the untyped form of the identity. **Rejected — journal/
subscription checkpoints as the stream's resume identity.** A
`ChangeCheckpoint` names a position in the *publication journal*, which is
upstream of the stream object and meaningless across compaction (D2); the
stream's own metadata is the authority for its bytes.

## D4 — Authorization model (PROPOSED)

**Chosen.** **Backend-supplied tenant context, engine-enforced isolation, no
engine authorization surface** — the PD-2026-08-26-11 rule applied verbatim.
Every AI-era stream operation binds to a validated `TenantScope`; the
engine's obligation is structural isolation (reserved records live in the
tenant's own keyspace; reads validate the scope before any record is paged,
the same `validated_identity` discipline as the backbone). **Single-writer is
engine-enforced as a correctness rule, not an authorization rule**: the
metadata records the open writer, a second writer is refused with a typed
error while the stream is open (InstantDB's "one writer per `clientId`"
observable), and each append stages the tail segment with an exact expected
version so a race surfaces as a typed conflict, never silent loss.

**Why the split is load-bearing.** Ordering is a database invariant: under
ADR-0004 D-3 (last-writer-wins on a bounded hybrid logical clock,
`docs/adr/0004-w4-replication-acknowledgement-and-conflict-decisions.md:67`)
two uncoordinated appenders writing the same tail record would be resolved by
clock, i.e. one appender's bytes would clobber the other's with no error. So
*serialize the writers* is an engine decision (like identity uniqueness), and
*who is allowed to be the writer* is a Platform decision (like customer
auth). The exact-version append (`src/types.rs:390`, `ExpectedVersion`) is
defense in depth in the same shape V2 uses everywhere else: divergence is a
typed conflict record, never a guess.

**Rejected — InstantDB-style permission rules on the stream namespace**
(EV-I10: rules default `"false"` on `$streams`). An engine-side rule
evaluation surface for streams would create the authentication/authorization
surface PD-2026-08-26-11 D1 removes from this boundary, would apply to one
family kind and not the rest, and would duplicate the Platform's PB-3042
plane. **Rejected — per-stream authorization embedded in stream metadata.**
Same objection, plus it makes the authorization data mutable by the stream
writer.

**Disclosed limit (standing, not stream-specific).** Until the Platform's
authorization plane lands (PB-3042, engine-side adapter dispatch W5-E #57),
the finest isolation V2 enforces anywhere is the tenant: any principal the
backend scopes to a tenant can read and append that tenant's streams, exactly
as it can read and write that tenant's documents. This is the recorded
engine-wide boundary decision, restated here so CP-109's authorization row is
a disposition, not silence.

## D5 — First-class stream object vs composition (PROPOSED)

**Chosen.** A **first-class stream object with composed delivery**. The
object — typed create / append / read-range / seal / abort / metadata
operations at the streaming-family seam, with state in reserved family
records (D1, D2) and resume by typed checkpoint (D3) — is first-class, and
its *delivery* composes the seams that already exist: an append's commit
publication emits a **content-free Change Notification** (PD-2026-08-26-13
D3 — push the hint, pull the delta) so consumers fetch the next byte range
immediately instead of polling, and the bytes themselves are served by the
typed range read, never carried by the notification. Backbone subscription of
stream progress is the same shape: a filtered change event that names the
stream and its sealed length, with no document bodies in events
(`src/streaming/backbone.rs:383` — a `ChangeNotification` carries exactly two
identifiers; the SE-03 content-free proof).

**Why not pure composition** (stream chunks as ordinary documents in a user
collection; consumers via existing subscriptions and the backbone; metadata
as a user document). Four reasons, in force order:

1. *Write amplification against a measured-budget objective.* A document per
   token batch means a journaled commit, a document identity, a version, and
   a change event per batch — the exact cost PD-2026-08-26-13 D1 exists to
   bound (bytes per delivered change, per-frame envelope overhead). A
   segmented stream with tail-append amortizes records to one per segment
   bound, and notification-driven range reads amortize delivery to one hint
   per commit. InstantDB's in-memory buffer with periodic flush is the same
   amortization done durably worse (EV-I10); V2 should amortize *inside* the
   durable substrate rather than before it.
2. *No ordering contract.* Ordinary document writes are commutative under
   ADR-0004 D-3; chunk order would rest on client-supplied fields with no
   engine enforcement, and interleaved concurrent writers could not be
   refused (D4's clobber argument).
3. *No byte addressability.* The substrate holds no index from byte offset to
   record, so "resume from byte N" becomes a scan or a client-side
   reconstruction.
4. *Unenforced metadata.* `done`/`size`/`abortReason` become user data again
   (D1's rejected alternative).

**Why not a new storage engine either.** The capability needs no new storage
format, no new commit path, and no new replication story: reserved records,
transactions, snapshots, and family-record replication already provide them.
The first-class part is the *object and its operations*, not a new substrate
— the same resolution the attachment content plane reached for bytes
(`docs/families/attachment-content-plane.md` D1's rejected bytes-carrying
`DocumentValue` alternative).

**Explicitly not a prohibition.** Pure composition stays available to any
caller: nothing forbids storing chunks in ordinary collections and
subscribing to them. The decision fixes what the engine ships as the CP-109
capability and what its measured better-than claim attaches to; it takes no
capability away.

## Why no V1 parity row is opened (V1 pointers, verified at the pin)

The packet brief's V1 pointer — "V1 has no AI-era stream primitive" — was
verified rather than assumed, by looking where V1's adjacent capabilities
live:

- **V1's durable byte storage is write-once and hash-addressed.**
  [v1:src/TerraBase.Server/Documents/AttachmentsStorage.cs::PutAttachmentStream]
  writes an attachment stream keyed by its content hash (line 702) and
  [v1:src/TerraBase.Server/Documents/AttachmentsStorage.cs::GetAttachmentStream]
  reads it back (line 1003). There is no append-after-write, no writer
  identity, no progress metadata namespace, and no resume contract: V1's
  range-read surface serves whole immutable content, not a live tail, so
  InstantDB's `byteOffset` reads (EV-I10) have no V1 counterpart.
- **V1's AI surface streams through the server, not into it.** The AI agent
  handler pipes a model provider's SSE response into the HTTP response body
  (`text/event-stream`,
  [v1:src/TerraBase.Server/Documents/Handlers/AI/Agents/AbstractAiAgentProcessor.cs:61-64]).
  Nothing is journaled, nothing is resumable, and the handler's own "resume"
  is conversation-turn resume — the caller must re-send prior
  `ActionResponses`
  ([v1:src/TerraBase.Server/Documents/Handlers/AI/Agents/RequestBody.cs::ValidateForResume],
  lines 50-56) — not durable stream resume from a stored position.
- **V1's resumable delivery surface is change delivery**, resumable by change
  vector
  ([v1:src/TerraBase.Client/Documents/Subscriptions/AbstractSubscriptionWorker.cs:764]),
  which V2 already covers through the SE-03 backbone and the PB-6002..6009
  rows.

So the capability is competitive (EV-I10) and agent-native
(PD-2026-08-26-07 D2), not V1 parity: no PB row is opened by this record, and
if the parent wants one for the AI-era stream object it is a parent-owned
WS-PARITY filing, not a builder action.

## Better-than measure for CP-109 (what must be measured, not asserted)

The matrix's measure
(`docs/competitive/matrix.md:66`): "Streams backed by the engine's own
storage and change backbone (one product, one consistency model) vs
InstantDB's in-memory buffer with periodic flush (EV-I10); resume measured
under PD-13 byte budgets." This record makes that measurable, and claims
nothing:

- **Durability at every append, no flush window.** InstantDB buffers written
  data in memory and flushes periodically (EV-I10, verbatim), so an
  unflushed append is lost on server loss. Under D2, every accepted append
  is durable at its commit. Phase 2 must prove this with a recovery-lane
  receipt (append → crash/reopen → read-back includes the acknowledged
  append) — capability evidence, not a timing claim.
- **One consistency model.** Stream bytes, metadata, and the notification
  that wakes consumers all live in one engine transaction and one
  publication. The competing design (buffer + flush) has two. Phase 2 must
  show the notification never precedes the bytes it announces (ordering
  proof at the seam).
- **Resume under byte budgets.** Bytes-on-wire to resume from an arbitrary
  checkpoint, per-frame envelope overhead of stream frames, and
  trigger-notification size are measured against PD-2026-08-26-13 D1 budgets
  (SE-07/SE-15), with PD-2026-08-26-03 receipts and verdict vocabulary for
  any performance statement.

No latency, throughput, or byte figure is asserted by this record, and none
may be asserted by phase 2 without its receipt.

## Phase-2 implementation ticket shape (OUT of this packet)

Sketch only — the parent files, amends, or rejects it. Phase 2 starts only
from ratified decisions.

**Title (suggestion).** GAP-C3 phase 2 — AI-era streams: durable, resumable
stream object at the streaming-family seam (CP-109).

**Dependencies (blocked-by).**

- This record, ratified (D1..D5).
- SE-03 #5 (landed) — notification and backbone surfaces it composes with.
- Family seam and the GAP-F01 #71 content-plane encoding (implemented) —
  segment encoding and reader discipline it reuses; **not** its byte
  ceiling (D2's obligation).
- SE-04 #6 (terrascale.sync.v2) — client resume frames carry the stream
  checkpoint; the checkpoint type must be serializable into that vocabulary.
- SE-14 #10 (TBEP) and SE-08 #11 (host) for any exposure beyond the engine
  seam; SE-13 #12 (CLI) optional; AI-01 #30 proves MCP parity for the new
  operations.
- PD-2026-08-26-03 / SE-15 #23 benchmark lane for every receipt; the recovery
  lane for the append-durability drill; GAP-F8 (family backup) coordination
  so stream records are covered by backup once that packet lands.

**Acceptance criteria (sketch).**

1. Typed create/append/read-range/seal/abort/metadata operations; engine-
   assigned identity; all MCP-exposable like every other typed operation.
2. Every accepted append durable at its commit; recovery-lane receipt for
   append → crash → reopen → read-back.
3. Resume from any byte position after reconnect **and** after process
   restart, by typed checkpoint; fail-closed on stale-epoch/past-end
   checkpoints with typed errors, never silent aliasing.
4. Single writer enforced: second writer refused with a typed error while
   open; concurrent append surfaces as a typed conflict, never silent loss.
5. Status/size/abort metadata queryable through typed operations and linkable
   to entities; the recorded size always equals the sealed bytes (an
   invariant test, not a convention).
6. Notifications on append are content-free, bounded, and never block a
   commit (the SE-03 proofs restated for this producer).
7. Tenant isolation: no stream operation observes another tenant's records;
   the red-then-green guard proof SE-03 used applies.
8. Its own typed bounds (max stream bytes, max segment count, max append
   size, max metadata links) with typed over-limit refusals — recorded as
   explicit limits, never silently inherited from the attachment ceiling.
9. PD-2026-08-26-13 D1 byte budgets measured: bytes per delivered stream
   chunk, per-frame envelope overhead, trigger-notification size, and the
   resume/catch-up byte budget; PD-2026-08-26-03 receipts for any
   performance claim.
10. Conformance vectors in CI; `scripts/check.sh` green.

**Write scope (suggestion) and predicted shared-file collisions.** State
module under `src/families/` (mirroring `src/families/attachments/`), delivery
additions in `src/streaming/` (the seam that already reads family state —
`src/streaming/sources.rs:13-14,49,257-271`), tests in-module plus
`tests/streaming/` wiring. Predicted collision actions, all to be proposed by
phase 2, none taken here: extending the closed `FamilyKind` set and
`family_key`/record classification in `src/families/mod.rs` and
`src/families/state.rs` (shared with the families wave); the `ChangeKind`
selector surface in `src/streaming/mod.rs` if stream progress becomes a
backbone kind; `src/lib.rs` re-exports; TBEP registry additions (SE-14);
`tests/streaming/main.rs` wiring. If the parent prefers a stream kind owned
from `src/streaming/` with the families files untouched, that is an
amendment to this record, not a phase-2 liberty.

## Deferred items (recorded, with owners)

- **TQL addressability of reserved records** (stream metadata in the query
  language, InstantDB `$streams` parity): TQL wave (#34 TQL-03 / parent).
  Until decided, "queryable" means typed operations.
- **Per-stream authorization rules beyond tenant isolation**: Platform
  PB-3042 / W5-E #57, per PD-2026-08-26-11 D1.
- **Host/SSE and SDK resume exposure**: SE-08 #11 (host endpoints, e.g. an
  InstantDB-style reconnect surface), SE-09/SE-10 (native clients), SE-04 #6
  (frame vocabulary).
- **MCP parity conformance for the new operations**: AI-01 #30.
- **Backup coverage of stream records**: GAP-F8 owner (family backup), with
  BK-03 logical-migration mapping if streams migrate.
- **Studio view of live streams**: DB wave, optional, only if a page earns
  its place — not a CP-109 obligation.
- **Retention/eviction policy beyond explicit deletion**: none proposed; any
  future bound must be a recorded decision, never a silent expiry.
- **`scripts/check.sh` workspace-root fallback stops two levels up**
  (surfaced by run 2 of this packet's receipts): the candidate loop tries
  `repo_root/..` and `repo_root/../..` only, so a worktree at
  `TerraBaseV2/.worktrees/<name>` resolves no root and the V1-citation audit
  exits "UNRUN". The supported `TERRASCALE_WORKSPACE_ROOT` variable is the
  workaround this packet used; a third candidate (or a `git rev-parse`-based
  walk) is a one-line change in a file this packet does not own. Owner:
  integrator / the scripts owner. No edit was made — the variable satisfies
  the receipt, and a scripts edit by a docs-only packet would be an
  unnecessary collision.

## Proposed collision actions

None. This packet wrote exactly one new file — this record — inside its
declared write scope. The write-scope check the packet was asked to make was
made: the competitive matrix's own record points to
`docs/competitive/gap-ticket-proposals.md`, the two survey files, and back to
`matrix.md` itself only for recording filed issue indexes
(`docs/competitive/README.md`, `docs/competitive/matrix.md` status summary) —
no `docs/competitive/` companion location is named for a decision record, and
the filed proposal's write-scope suggestion names "`docs/competitive/` or
`docs/streaming/`" (`docs/competitive/gap-ticket-proposals.md:99-100`), so
this record lives at the packet's named path and `docs/competitive/` is
untouched. The matrix row CP-109 is likewise untouched: flipping a
disposition is the parent's act, on ratified decisions. The collisions listed
in the phase-2 sketch are predictions for that packet to propose, not actions
taken here.

## Receipts

All commands were run in this worktree with the pinned toolchain
(`RUSTUP_TOOLCHAIN` unset first; `scripts/check.sh` does it internally).
No code changed, so the receipts prove the packet is inert against the gates
rather than green-by-construction of new code.

| Command | Exit |
| --- | --- |
| `unset RUSTUP_TOOLCHAIN; cargo fmt --all -- --check` | 0 |
| `unset RUSTUP_TOOLCHAIN; cargo check --workspace --locked` | 0 |
| `unset RUSTUP_TOOLCHAIN; cargo test --workspace --locked` (dev profile) | 0 |
| `unset RUSTUP_TOOLCHAIN; cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `unset RUSTUP_TOOLCHAIN; cargo doc --workspace --no-deps --locked` | 0 |
| `TERRABASE_V1_ROOT=…-readonly ./scripts/check.sh` (run 1) | 101 |
| `cargo test --workspace --locked --lib streaming::tests::publication_sink_never_blocks_a_commit_behind_a_wedged_poll` (run 1's failure, isolated) | 0 |
| `TERRABASE_V1_ROOT=…-readonly ./scripts/check.sh` (run 2) | 1 |
| `python3 scripts/validate_v1_citations.py --mode strict --v1-root …-readonly --workspace-root …/TerraScale --gap-report parity/v1-test-gaps.json` (run 2's failing step, isolated) | 0 |
| `TERRABASE_V1_ROOT=…-readonly TERRASCALE_WORKSPACE_ROOT=…/TerraScale ./scripts/check.sh` (run 3, authoritative) | 0 |

Every run is recorded, none retried into agreement silently. What happened:

- **Run 1 failed one test — a load flake.**
  `streaming::tests::publication_sink_never_blocks_a_commit_behind_a_wedged_poll`
  panicked at `src/streaming/tests.rs:1673` — "the commit blocked 1.360782326s
  behind a wedged subscriber poll" — in the dev-profile pass, with 807 other
  tests passing and several unrelated tests in the same run taking over 60
  seconds each while concurrent fleet builds compiled on this host. Re-run in
  isolation it passes in 0.85 s. This is the single timing-sensitive failure
  under load the packet brief names as a flake symptom, and it never
  reappeared (run 2's dev and release passes were green, zero failures).
- **Run 2 failed one environmental step — a script-input omission, not a
  test.** Everything through the release-profile test pass, clippy, doc, the
  parity tooling, the license check, the benchmark-receipt discipline, and
  the secret scan passed; the script then exited 1 at "V1 citation audit
  UNRUN: unable to resolve the TerraScale workspace root". Cause, reproduced
  deterministically: `scripts/check.sh` resolves the workspace root by trying
  `repo_root/..` and `repo_root/../..`, and this packet's worktree sits three
  levels below the workspace root
  (`TerraBaseV2/.worktrees/terrabasev2-gapc3`), so neither candidate holds
  `specs/` + `TerraBase/`. The script's supported `TERRASCALE_WORKSPACE_ROOT`
  variable is the intended input for that case; the packet's standard receipt
  command does not name it. Running the exact audit step with the root
  supplied passes (table row above).
- **Run 3 is the authoritative green run**: the full script with
  `TERRASCALE_WORKSPACE_ROOT` set, exit 0 end to end, including the
  strict-mode V1-citation audit against the pin and the secret scan.

The packet's substance is unaffected by all three: this packet adds one
markdown file and no Rust source, so it cannot have changed any test's
behavior; run 1's flake is in SE-03's test, whose subject this record
evaluates but whose code it does not touch. `cargo doc` reported 6 warnings
from the `terrabase-v2` lib docs (and 1 from `terrabase-tbep`); this packet
adds no Rust source and `cargo doc` does not read `docs/`, so it cannot have
produced them. `cargo-deny` is unavailable on this host; the script records
that check as unrun itself (`AGENTS.md`: record unavailable tools as unrun).
Exact transcripts live in the packet report, not in this record.

## Disclosed limits

- Every decision is PROPOSED. Nothing here authorizes phase 2, and no
  matrix or parity status changed.
- The dispatch carried no ticket body beyond the filed proposal; if the
  filed #85 text adds requirements this record missed, the parent should
  amend rather than have phase 2 absorb them silently.
- InstantDB's stream page was read through the landed survey (EV-I10,
  accessed 2026-08-26, sunset banner recorded once in
  `docs/competitive/matrix.md:13-16`); this record did not re-fetch the
  competitor docs, so it cannot speak to any behavior the survey did not
  record (e.g. stream size limits, flush intervals, concurrency ceilings).
- No measurement of any kind was taken: no timing, no byte counts, no
  benchmark. The better-than section names the measurements phase 2 owes.
- Whether TQL may address reserved records is undecided; D1's "queryable" is
  typed-operation queryability only.

## Explicit non-claims

- No claim that AI-era streams are implemented, designed in code, or
  achievable at any particular cost — this record decides direction only.
- No claim that durable-append-per-commit is fast, or faster than
  InstantDB's buffered append; any such statement requires a
  PD-2026-08-26-03 Benchmark Receipt, and none exists.
- No claim of Convex parity beyond what the survey verified: no
  storage-backed stream primitive was found there, and none is claimed
  absent.
- No claim that pure composition (chunks as documents) is impossible or
  forbidden — it is rejected as the engine's shipped capability, and remains
  available to callers.
- No claim of any V1 parity coverage: the V1 sections above establish an
  absence, and no PB row is touched.
- No claim about host, SDK, MCP, or studio surfaces — those are deferred
  with owners above.
