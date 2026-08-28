# Vector search at the query seam (GAP-C4)

Ticket: TerraBaseV2#86 (GAP-C4, CP-204 — the REQUIRED competitive row
per PD-2026-08-26-07 D2). Implementation: `src/query/vector.rs` (public
seam `terrabase_v2::query`); focused tests: `tests/query/vector_search.rs`
(public seam, 22 tests), `tests/query/vector_conformance_vectors.rs`
(conformance corpus, 3 tests over 32 named vectors), and
`src/query/vector_tests.rs` (in-crate execution semantics, 15 tests plus
a 5-vector semantic corpus). V1 parity anchors: PB-4024 (vector search)
and PB-4051 (vector index options and quantization), both classified
optional in `parity/contracts/query.json` and recorded required by the
competitive routing (CP-204, EV-C8).

V1 source identity for every anchor below: commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`, read from the pinned clean
read-only worktree reached through `TERRABASE_V1_ROOT`. Citations use
the parity form `[v1:path::Symbol]`.

This is a design/decision record in the line of
`docs/indexing/static-index-definitions.md`, not an ADR: ADR numbering
is parent-owned. Decisions the parent must ratify are labeled
**PROPOSED**.

## Continuation note

The previous builder of this packet died to a server error mid-build.
This packet adopts its committed work unchanged in intent: commit
`c598006` (the red tests at the public seam) and the untracked in-crate
execution corpus. Two latent defects in the committed red tests are
repaired here, both of which the red phase could not execute (its run
failed at `error[E0432]` unresolved imports, before any assertion
evaluated):

1. The conformance corpus carries **five** declaration vectors (32
   total), not the four (31) its exact-count assertion stated.
2. The `request/vector-empty` corpus vector built a valid four-dimension
   vector where its name and expectation (the empty-vector refusal)
   require the empty vector (`dimensions: 0`).

One committed assertion — `assert_eq!` against
`InvalidSimilarityThreshold { requested: f64::NAN }` — is unsatisfiable
under a derived `PartialEq` (NaN never equals NaN); the implementation
gives `VectorSearchError` a manual `PartialEq` whose threshold payload
compares NaN-equal, so the committed contract text holds as written.

## What landed

- **Declaration vocabulary (PB-4051)**: `VectorOptions` (dimensions over
  the closed 2..=4096 range, source/destination embedding types, ANN
  candidate/edge counts) and `VectorIndexDeclaration` (name under the
  static-index name rules, one ordinary collection, one field), with the
  content-addressed `identity_key` and the
  `Unchanged`/`OptionsChanged`/`IdentityChanged` drift model of GAP-Q06
  D10. Schema-optional per PD-2026-08-26-04 D1: nothing here makes a
  schema mandatory, and the execution treats documents without the
  vector field as non-candidates.
- **Filtered k-NN at the query seam (PB-4024, CP-204)**:
  `QueryStore::vector_search` executes a `VectorSearchRequest` against
  one immutable snapshot — exact cosine scoring over stored document
  vectors, the request's ordinary `QueryPredicate` applied as a
  pre-filter (the better-than measure vs Convex's actions-only
  restriction, EV-C8), a minimum-similarity threshold, limit 1..=256,
  bounded deadline/cancellation checkpoints, and stage accounting
  (`scanned`, `filter_skipped`, `missing_field`, `scored`,
  `threshold_dropped`, `returned`).
- **Deterministic result metadata**: matches carry id, score, and
  document; order is score descending with ties broken by document
  identity — pinned by test against a corpus whose ids are committed in
  reverse order and re-executed against the same snapshot.
- **Typed errors**: the full `VectorSearchError`/`VectorOptionsError`/
  `VectorIndexError` taxonomy, including the dimension mismatch,
  empty/non-finite query vector, invalid limit and threshold, and the
  fail-closed malformed-stored-vector class. No error carries a document
  value or identity.
- **TQL predicate handoff**: `TqlVectorPredicate` (`vec/1.0`) renders
  the `tql/1.0` grammar text of `docs/tql/spec.md` 8.2 and lowers a
  bound parameter value into the engine's typed request. The grammar
  integration rides TQL-03 (#34) — see the seam handoff below.
- **Conformance vectors in CI**: `tests/query/vector_conformance_vectors.rs`
  follows the golden-vector pattern of `tests/query/tql_golden_vectors.rs`
  — 32 named construction vectors with exact-count assertions and
  refusal-variant coverage checks that fail when a refusal class loses
  its last producing vector.

## Decision records

### D1 — Substrate: exact scoring over stored vectors (PROPOSED for
parent ratification)

The first landing scores **exactly, by brute force, over the vectors
stored in document fields** of the snapshot view — the GAP-Q6 field
storage plane as substrate (`docs/indexing/static-index-definitions.md`
D6 names vector options as a row that composes with that contract
later): the execution materializes the snapshot's documents exactly as
the index-less query path does (`Engine::snapshot_documents`), reads the
declared field, and scores it. Deterministic ordering (score descending,
identity tie break) is part of the same decision: an exact pass has a
total order an ANN structure cannot promise.

- **Alternative rejected for now — an HNSW-class ANN structure** (V1's
  substrate: [v1:src/TerraBase.Voron/Data/Graphs/Hnsw.cs::Hnsw]): the
  declaration already carries and validates V1's graph-construction
  vocabulary (`NumberOfCandidatesForIndexing`, `NumberOfEdges`;
  [v1:src/TerraBase.Client/Documents/Indexes/Vector/VectorOptions.cs::VectorOptions] at
  `Validate`, lines 74–83, refuses non-positive counts), so adding the
  structure later is an additive change behind the same declaration —
  but it would couple this ticket to index build/maintenance machinery
  (segments, generations, rebuild-on-drift) that is IX-wave work, and it
  would surrender the determinism pin the contract leads with.
- **Why exact is acceptable**: the competitive row's measure is
  capability (dims 2..4096, ANN-class cosine, filter fields, limit
  1..256 — EV-C8), not latency; the ticket names brute-force-exact +
  deterministic ordering an acceptable first step. **No performance
  claim is made** (see non-claims); the ANN follow-up is a named
  deferred item.
- **Follow-up recorded**: ANN structure over the landed declaration
  vocabulary, owner WS-INDEX/IX (the #38 explicit-vs-auto precedence
  lane, which GAP-Q06 R3 already names for catalog lowering).

### D2 — Score semantics: cosine similarity, V1's output shape

The score is cosine **similarity** in [-1, 1]. V1 scores cosine
*distance* internally
([v1:src/TerraBase.Voron/Data/Graphs/Hnsw.SimilarityMethods.cs::Hnsw.SimilarityMethods] at
`CosineSimilaritySingles` line 16 / `CosineDistanceSingles` line 22) and
converts distance to similarity before reporting
(`DistanceToScoreCosine`, line 126 — `1 - distance`; dispatched by
[v1:src/TerraBase.Voron/Data/Graphs/Hnsw.cs::Hnsw] at
`DistancesToScores`, line 259). V2 scores similarity directly in f64
and reports it — the same number the V1 client sees. The
`minimumSimilarity` threshold of V1's client seam
([v1:src/TerraBase.Client/Documents/Session/IAbstractDocumentQuery.cs::IAbstractDocumentQuery] at
`VectorSearch`, line 373) is carried as the inclusive-range
`with_minimum_similarity` validation. Two defined edge rules V1 leaves
to floating-point behavior are pinned typed here: a zero-magnitude
vector scores exactly `0.0` (a defined answer, never NaN), and any
non-finite intermediate (a magnitude overflow) fails closed as
`InvalidStoredVector` rather than producing a NaN score that would make
the ordering non-total.

### D3 — Declaration and drift model (adopt of the GAP-Q06 D10 pattern)

Names follow the static-index rules (V1 alphabet, no double slash, no
misplaced dot, ≤255 bytes, reserved `Auto/` namespace refused
case-insensitively —
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore] at
`IsValidIndexName`, line 1125); identity is (name, collection, field)
with options as content; `identity_key` renders the full content — the
construction counts participate, which the tests pin; drift separates
`OptionsChanged` (rebuild required) from `IdentityChanged` (a different
index, no obligation to this one). The reserved family collection is
refused exactly as query sources refuse it.

### D4 — Quantization and auto-embedding: deferred, typed (PROPOSED)

PB-4051's quantization surface is dispositioned **deferred, fail-closed
typed**: every Int8/Binary configuration refuses with
`VectorOptionsError::QuantizationUnsupported`, mirroring V1's refusal of
re-quantization ([v1:...VectorOptions.cs::VectorOptions] at `Validate`,
lines 79–80) while extending it honestly — V2 scores f32 embeddings
only, so a quantized configuration would be silently mis-scored, not
merely redundant. A Text source refuses with
`AutoEmbeddingUnsupported`: V1's embedder sets Text dimensions
internally (line 78, "Dimensions are set internally by the embedder"),
but V2 dimensions are required and typed at construction, so
auto-embedding cannot be expressed yet; queries bind vectors by value,
which
`docs/tql/spec.md` 8.2 independently pins as the recommended TQL form.
Owners: quantization rides D1's ANN follow-up (WS-INDEX/IX);
auto-embedding rides the AI-01 (#30) embeddings-task integration.

### D5 — TQL predicate seam handoff (TQL-03, #34)

The predicate shape — field, `$parameter`, optional threshold, optional
limit — lands typed and versioned (`vec/1.0`), renders exactly the
grammar text `docs/tql/spec.md` 8.2 fixes, and lowers a bound parameter
value into the engine's `VectorSearchRequest` (integer and number
encodings lower to the same f32 embedding; non-vector and non-finite
bindings refuse typed). **What does not land**: the parser wiring —
grammar production, `$parameter` binding in the TQL compiler, and the
`order by score() desc` composition. The TQL grammar files are #34
(TQL-03)'s; this packet did not edit them. Handoff: TQL-03 consumes
`TqlVectorPredicate::render`'s text as the lowering target and
`TqlVectorPredicate::lower` as the lowering path; the default k of a
bare predicate (`DEFAULT_VECTOR_SEARCH_LIMIT`, 10) is an initial engine
value the TQL-03 paging model may revisit.

### D6 — Execution bounds follow the include read-window pattern

A request without a bound deadline gets a fresh one-minute window
(`VECTOR_SEARCH_WINDOW`) and one without a bound cancellation gets a
fresh uncancelled signal — the pattern of `src/query/includes.rs`
(`INCLUDE_READ_WINDOW`), so no execution is unbounded by default.
Cancellation is checked before execution begins, every 64 scanned
documents, and before the ordering pass — the checkpoint cadence of
`execute.rs`. A pre-cancelled signal binds freely and refuses at
execution (unlike `QuerySpec::try_new`, which refuses at construction);
the vector request's builders never validate reachability, its
execution does.

## WS-PARITY proposals (PB-4024 / PB-4051)

No parity overlay is mutated by this packet. Proposals for the parent's
disposition, in the line of `docs/parity/gapq06-ws-parity-proposals.md`:

- **PB-4024 (vector search) — propose `partial / V2-native-typed`**:
  landed at the query seam with exact cosine k-NN, ordinary-predicate
  filtering, deterministic ordering, typed dimension/limit bounds, and
  the similarity threshold. V1 surface verified at the pin: the client
  seam `VectorSearch(minimumSimilarity, numberOfCandidates, isExact)`
  ([v1:src/TerraBase.Client/Documents/Session/IAbstractDocumentQuery.cs::IAbstractDocumentQuery] line 373),
  the input binding and empty-input refusal
  ([v1:src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.cs::AbstractDocumentQuery] lines 1598–1635,
  refusal at 1629), and the distance-to-score conversion
  ([v1:src/TerraBase.Voron/Data/Graphs/Hnsw.SimilarityMethods.cs::Hnsw.SimilarityMethods] line 126).
  Not yet carried: approximate mode (D1 follow-up), text/vector dual
  input and multi-vector search, quantized scoring, score-vs-distance
  output selection. Those keep the row out of `full`.
- **PB-4051 (vector index options and quantization) — propose
  `partial / V2-native-typed`**: dimensions, embedding types,
  candidate/edge counts, and every validation refusal of V1's
  `VectorOptions.Validate` landed typed
  ([v1:src/TerraBase.Client/Documents/Indexes/Vector/VectorOptions.cs::VectorOptions] lines 74–83;
  [v1:src/TerraBase.Client/Documents/Indexes/Vector/VectorEmbeddingType.cs::VectorEmbeddingType] lines 6–27).
  Not yet carried: auto-embedding source fields and task identifiers,
  quantization (both D4 deferrals), and index-execution agreement beyond
  the exact mode (the declaration does not yet drive any index build).

## Deferred items (named owners)

- **ANN structure** behind the landed declaration vocabulary — WS-INDEX
  (IX wave, #38 lane). The substrate decision D1 is PROPOSED for
  ratification with this.
- **Quantization (Int8/Binary scoring)** — WS-INDEX (IX wave), rides the
  ANN substrate.
- **Auto-embedding (Text source)** — AI-01 (#30) embeddings-task
  integration.
- **TQL grammar integration of `vector.search`** — TQL-03 (#34); the
  handoff shape is D5.
- **Composition with the static index language** (vector options as an
  `idx/1.x` field option, per GAP-Q06 D6) — WS-INDEX (IX wave).
- **Retrieval-workload benchmark receipts** — the benchmark lane is held
  by IX-04; no PD-03 receipt exists and none is claimed. Owner: the
  benchmark lane ticket when it opens.
- **Operation/MCP exposure** (`PublicOperation` surface for declaration
  and search) — AI-01 (#30) consumes this vocabulary; wiring the
  operations is that ticket's.

## Proposed collision actions

- `src/query/mod.rs` (commit `7b020c0`, titled `collision:`): the module
  declaration `mod vector;`, its `pub use` re-export list, and the
  `#[cfg(test)] mod vector_tests;` declaration. Ten added lines, nothing
  else changed in the module root. The GAP-Q14 precedent (an integrator
  pre-wires the module declaration) was unavailable: the integrator rider
  `d2ba76f` did not pre-wire `vector`.
- `src/query/spec.rs`, `src/index/definitions.rs`: **not edited** — the
  field-name rule is consumed through the existing `pub(crate)`
  `validate_field_name`, and the name bound through the existing `pub`
  `MAX_NAME_BYTES`; no edit to either shared file was required.

## Disclosed limits

- Execution materializes the snapshot's document list and scans it —
  the same index-less base path W3-2 records for ordinary queries. On a
  large collection an exact k-NN scores every candidate vector; the
  bounded checkpoints are the only guard. No claim about scan cost is
  made or measured here.
- A stored vector field present with a **null** value is malformed (fail
  closed, `InvalidStoredVector`); only an **absent** field is a
  non-candidate. The tests pin the absent-field rule; the null-field
  rule is the implementation's documented strict reading.
- The execution fails the whole search on the first dimension-mismatched
  or malformed stored vector of the source collection — fail-closed by
  design; it does not skip the offender.
- `VectorSearchMode::Approximate` exists as typed vocabulary with no
  producing path; `request.mode()` is always `Exact`.
- The similarity measure is cosine only (`VectorSimilarity::Cosine`).

## Explicit non-claims

- No ANN, no approximate execution, no recall claim.
- No quantization, no auto-embedding.
- **No performance claim of any kind** — nothing here is "faster" or
  comparable to any engine; a retrieval-workload claim requires a PD-03
  Benchmark Receipt from the benchmark lane, and none exists.
- No parity overlay is mutated; PB-4024/PB-4051 rows stay as classified
  until WS-PARITY dispositions them from the proposals above.
- The bounds (`MAX_VECTOR_SEARCH_LIMIT` 256, dimension range 2..=4096,
  `DEFAULT_VECTOR_SEARCH_LIMIT` 10, `VECTOR_SEARCH_WINDOW` one minute)
  are initial engine values, tunable with measured evidence.

## Test evidence

- Public seam (`tests/query/vector_search.rs`, 22 tests): declaration
  construction and validation incl. reserved namespace and family
  collection; identity key determinism and content addressing; drift
  separation; dimension bounds at the competitive bar; limit bounds;
  empty/out-of-bounds/non-finite query vectors; threshold range;
  filter composition; declaration binding; TQL render, versioning,
  lowering, and its refusals; deadline/cancellation binding.
- Conformance corpus (`tests/query/vector_conformance_vectors.rs`,
  32 vectors, 3 tests): every accept and refusal class of the
  construction surface has a named vector; exact-count assertions pin
  the corpus size; refusal-variant checks fail when a class loses its
  last producing vector.
- In-crate execution (`src/query/vector_tests.rs`, 15 tests + 5-vector
  semantic corpus): the pinned cosine values (1, 0, −1, −√2⁻¹); the
  zero-magnitude rule; descending order; the identity tie-break
  determinism pin (reverse-committed ids, re-executed); k truncation
  with accounting; pre-scoring filter; schema-optional missing fields;
  collection isolation; dimension mismatch; the three malformed-stored
  -vector shapes; threshold drop; cancellation; foreign-snapshot
  refusal; the document payload beside id and score.
- Red-then-green: the red run is the previous builder's record (commit
  `c598006`: `cargo test` failing with exactly two `error[E0432]`
  unresolved imports, exit 101). Green: all 40 vector tests pass; the
  full workspace suite is green (see receipts).

## Receipts

All commands run inside the packet worktree
`.worktrees/terrabasev2-gapc4` on branch `fleet/gapc4-2`, after
`unset RUSTUP_TOOLCHAIN`:

| Command | Exit |
| --- | --- |
| `cargo fmt --all -- --check` | 0 |
| `cargo check --workspace --locked` | 0 |
| `cargo test --workspace --locked` (dev profile, REQUIRED) | 0 — 885 lib tests, 81 query tests, all targets green, 0 failed |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `TERRABASE_V1_ROOT=/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly ./scripts/check.sh` | 0 — includes the release-profile test pass (green), rustdoc, parity tooling (4 + 13 tests, OK), benchmark-receipt discipline (9 receipts), license check, and secret scan |

One stage inside check.sh reports itself UNRUN: the V1 citation audit
prints "unable to resolve the TerraScale workspace root" when invoked
from this nested worktree (`.worktrees/terrabasev2-gapc4`), so the
automated audit did not execute. Compensating evidence: every
`[v1:...]` citation in this record was verified by direct reads of the
pinned tree at `fa99b9257776dbe625e6a2388c74c9ce916637ee` during this
packet (the file:line anchors above are those reads). The parent may
re-run the audit from the main checkout where the root resolves.
