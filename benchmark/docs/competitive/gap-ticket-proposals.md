# CP-01 proposed gap tickets (for the parent to file)

Status: FILED 2026-08-26 by the parent — GAP-C1 = #83, GAP-C2 = #84, GAP-C3 = #85, GAP-C4 = #86 (indexes recorded in matrix.md). Historical proposal text follows. Originally: proposals only, per ticket TerraScale/TerraBaseV2#29 (CP-01) —
"Gap-ticket FILINGS are parent-owned: propose them (titles, bodies, deps)
in a file under docs/competitive/; do not file anything." No Forgejo
object was created or edited by this file. Numbering follows the PS-wave
convention (GAP-D*/Q*/F*/S*/A*/R*/P* already proposed by PS-01..PS-04);
these are GAP-C* (competitive). Once filed, record the issue indexes in
`docs/competitive/matrix.md` (each row names its ticket).

Naming: GAP-C1 proposes wave `OL` (offline). GAP-C2..C4 are engine
tickets. Dependency edges use existing issue indexes where they exist
and proposed-ticket names where they do not.

## GAP-C1 — Offline persistence wave (OL): durable client stores, offline
mutation queues, and resync (matrix row CP-106; REQUIRED)

- Blocked by: #6 (SE-04 terrascale.sync.v2 — offline queue frames and
  checkpoint resume ride the versioned wire contract), #7 (SE-05
  conformance kit — offline vectors must run in every SDK CI),
  Terrascale.TypeScript#11 (SE-09) / Terrascale.DotNet#9 (SE-10) as the
  implementing clients. Soft edge: #4 (SE-02) — the sync-core decision
  determines whether the durable store wraps an adopted base engine.
- Decision context (why this ticket exists): the charter ledger
  2026-08-26 recorded the user's "memory-only client stores this wave,
  durable offline as a follow-up (confirmed)" decision (map issue #2).
  PD-2026-08-26-06 D4 and #29 supersede its timing: offline persistence
  is a required competitive row, scheduled as this wave — not an
  indefinite deferral. Competitor evidence: InstantDB ships offline
  persistence (EV-I4); Convex does not (survey "not verified").
- What it builds: a durable per-client store surviving process restart;
  an offline mutation queue whose entries carry idempotency keys and
  survive restart; resync from the Sync Checkpoint on reconnect with
  typed conflict outcomes on divergence; storage-size and eviction
  bounds per client; conformance vectors (offline write, restart,
  reconnect-resync, duplicate suppression) executed in each SDK CI per
  PD-2026-08-26-12 (native-language clients; no wasm/wasi, no C ABI
  client embeddings).
- Write scope (suggestion): SDK-side store modules in both SDK repos;
  protocol frame additions (if any) coordinated with SE-04; vectors in
  the SE-05 kit; docs under docs/sync/ in this repo.
- Acceptance criteria (suggestion): offline writes are durable across
  client restart; resync replays exactly once per idempotency key
  (duplicate delivery applies each operation once); divergence surfaces
  as typed conflict records, never silent loss; golden vectors green in
  both SDK CIs; bytes-on-wire budget for resync measured under
  PD-2026-08-26-13; any speed claim carries a PD-2026-08-26-03
  Benchmark Receipt; scripts/check.sh green.
- Better-than measure (CP-106): offline on a multi-region engine —
  resync convergence receipts across Regions plus measured resync byte
  budgets; InstantDB's offline is single-cloud by construction.

## GAP-C2 — Presence and ephemeral channels at the sync seam (matrix rows
CP-107, CP-108)

- Blocked by: #6 (SE-04 — presence and topic frames join the sync.v2
  vocabulary), #11 (SE-08 — host surfaces the operations). Soft edge:
  #5 (SE-03 backbone) if presence observations are also exposed to
  backbone consumers.
- Competitor evidence: InstantDB ships rooms/presence/topics (EV-I8);
  Convex does not (survey "not verified").
- What it builds: room-scoped presence state (join/leave/heartbeat,
  automatic cleanup on disconnect, room-scoped authorization context)
  and fire-and-forget typed topic frames (no persistence by
  construction, bounded buffers), both riding existing Sync Sessions —
  no separate realtime service, no broker in the hot path
  (PD-2026-08-26-13 discipline applies to envelope overhead).
- Write scope (suggestion): sync-seam modules and tests in this repo;
  frame additions coordinated with SE-04; vectors in the SE-05 kit;
  docs under docs/sync/.
- Acceptance criteria (suggestion): presence cleanup proven on abnormal
  disconnect; topics never persisted and never block a commit; frame
  envelopes within PD-13 byte budgets; conformance vectors in the kit;
  scripts/check.sh green.
- Better-than measure (CP-107/CP-108): presence and channels on the same
  sessions, checkpoints, and transports as data — one product, one
  consistency model; capability evidence is the conformance set.

## GAP-C3 — AI-era streams: evaluation and decision record, then
implementation (matrix row CP-109; evaluation row per PD-06 D4)

- Phase 1 (decision) blocked by: #5 (SE-03 backbone — the replay
  substrate streams would build on) and the storage family seam
  (PB-3000..3008 status per PS-03 #55; GAP-F1 content plane if stream
  bytes land in the content plane). Phase 2 (implementation) blocked by
  phase 1's decision and #6 (SE-04) for client resume frames.
- Competitor evidence: InstantDB ships durable, storage-backed,
  resumable streams marketed for LLM output (EV-I10); Convex lists
  agent-component streaming only (EV-C1 index) — no storage-backed
  stream primitive verified.
- What it evaluates: stream identity and metadata surface (queryable,
  linkable to entities — InstantDB's `$streams` is the precedent);
  persistence substrate (storage family vs journal windows); resume
  semantics (byte-offset vs checkpoint); authorization model; whether
  the capability composes from existing seams (change backbone +
  storage) or needs a first-class stream object. The decision record
  must state the alternative rejected and why, then the implementation
  ticket proceeds.
- Write scope (suggestion): docs/competitive/ or docs/streaming/
  decision record; then engine implementation at the streaming seam
  with conformance vectors.
- Acceptance criteria (suggestion): decision record with named
  alternatives and evidence; if built — durable stream writes, resume
  from any offset after reconnect, metadata queryable as typed
  operations, PD-13 byte budgets for stream frames, vectors in CI;
  scripts/check.sh green.
- Better-than measure (CP-109): streams backed by the engine's own
  storage and backbone (one consistency model) versus InstantDB's
  in-memory buffer with periodic flush (EV-I10); measured, not asserted.

## GAP-C4 — Vector search at the query seam (matrix row CP-204; REQUIRED
per PD-2026-08-26-07 D2)

- Blocked by: GAP-Q6 (index field storage plane — proposed by PS-02
  #54, parent adjudication pending) as the hard substrate edge;
  coordinate with GAP-Q13 (analyzer foundation) if index plumbing is
  shared. Composes with #34 (TQL-03) for the typed predicate surface
  (PD-2026-08-26-08 D1 names "search and vector predicates as
  functions"). Feeds #30 (AI-01) — its acceptance explicitly waits on
  "the vector-search gap ticket from CP-01".
- V1 parity anchor: PB-4024 (vector-search) and PB-4051 (vector-index
  options and quantization) — classified optional in the contract but
  recorded by PS-02 #54 as required CP rows routed via CP-01; PS-02
  verified both absent in the tree today.
- Competitor evidence: Convex ships vectorIndex + `ctx.vectorSearch`
  (EV-C8: dims 2..4096, ANN cosine, filter fields, actions-only,
  1..256 limit); InstantDB does not ship vector search (survey "not
  verified").
- What it builds: engine-side vector index declaration and maintenance
  on ordinary collections; filtered k-NN search callable from the query
  seam (not from a functions-only context); typed TQL vector predicates;
  deterministic result metadata (ids + scores); conformance vectors;
  PB-4024/PB-4051 dispositions through WS-PARITY when the seam lands.
- Write scope (suggestion): src/query/ or a dedicated vector module at
  the query seam, tests first; TQL predicate additions coordinated with
  the TQL wave; docs under docs/query/.
- Acceptance criteria (suggestion): filtered k-NN with typed errors and
  deterministic ordering; index declaration/evolution as typed
  operations (MCP-exposable, AI-01); schema-optional — vectors on
  ordinary collections without mandatory schema adoption; conformance
  vectors in CI; any performance claim carries a PD-03 Benchmark
  Receipt on retrieval workloads; scripts/check.sh green.
- Better-than measure (CP-204): vector search callable from the query
  seam and TQL directly (Convex restricts it to actions, EV-C8),
  filterable by ordinary predicates, on a multi-region engine —
  capability evidence plus receipts, no asserted numbers.

## Filing notes for the parent

- All four are engine-side except GAP-C1's client halves (SDK repos);
  none touches the functions-runtime ban (PD-2026-08-26-06 D3).
- GAP-C1 and GAP-C4 carry REQUIRED status (CP-106, CP-204); GAP-C3 is
  an evaluation-first ticket by PD-06 D4; GAP-C2 is engine gap with no
  dependency on Platform decisions.
- After filing, update `docs/competitive/matrix.md` rows CP-106,
  CP-107/108, CP-109, CP-204 with the issue indexes, and record the
  filed set in the charter ledger as CP-01's gap-ticket output.
- Existing proposals from PS-01..PS-04 (GAP-D/Q/F/S/A/R/P, 62 tickets)
  are untouched by this file; GAP-C4's GAP-Q6 edge assumes that ticket
  is filed as proposed — if the parent rejects GAP-Q6, GAP-C4's
  substrate edge moves to whatever ticket owns the index storage plane.
