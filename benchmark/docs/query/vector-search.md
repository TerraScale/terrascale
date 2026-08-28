# Vector search at the query seam (GAP-C4)

Ticket: TerraBaseV2#86 (GAP-C4, CP-204 — the REQUIRED competitive row
per PD-2026-08-26-07 D2). Implementation: `src/query/vector.rs` (public
seam `terrabase_v2::query`); focused tests: `tests/query/vector_search.rs`
(public seam, 25 tests), `tests/query/vector_conformance_vectors.rs`
(conformance corpus, 3 tests over 36 named vectors), and
`src/query/vector_tests.rs` (in-crate execution semantics, 19 tests
including a 5-vector semantic corpus). V1 parity anchors: PB-4024
(vector search) and PB-4051 (vector index options and quantization),
both classified optional in `parity/contracts/query.json` and recorded
required by the competitive routing (CP-204, EV-C8).

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
  bounded deadline/cancellation checkpoints with the default
  one-minute window **materialized at entry and enforced at every
  checkpoint** (the include read-window pattern; repair loop 1 made
  this true in code), and stage accounting (`scanned`, `filter_skipped`,
  `missing_field`, `scored`, `threshold_dropped`, `returned`).
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
  the `tql/1.0` grammar text `docs/tql/spec.md` 8.2 fixes for the
  `vector.search(field, $parameter [, threshold])` where-clause and its
  `limit` line — the `order by score() desc` composition rides TQL-03
  (#34) — and lowers a bound parameter value into the engine's typed
  request. Its field and parameter names are bare TQL identifiers
  (repair loop 1): grammar metacharacters refuse typed instead of
  rendering into the grammar text.
- **Conformance vectors in CI**: `tests/query/vector_conformance_vectors.rs`
  follows the golden-vector pattern of `tests/query/tql_golden_vectors.rs`
  — 36 named construction vectors with exact-count assertions and
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
the ordering non-total. Repair loop 1 adds the explicit range clamp: a
finite score is clamped into [-1, 1] before it is reported, because the
raw quotient of an exact match can sit one ulp outside the range
(`3 / (sqrt(3)·sqrt(3))` is `1.0000000000000002`); a threshold of
exactly 1.0 keeps an exact match.

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
(`INCLUDE_READ_WINDOW`): the bounds are **materialized once, at
execution entry** (deadline defaulting into the window, cancellation
defaulting to a fresh signal), and every checkpoint below enforces the
materialized bound, so no execution is unbounded by default. The
checkpoints fire at entry — **before the snapshot view is
materialized**, so an already-dead or already-cancelled request refuses
before any data access — then after the view materialization, every 64
scanned documents, and before the ordering pass: the checkpoint cadence
of `execute.rs`. A pre-cancelled signal binds freely and refuses at
execution (unlike `QuerySpec::try_new`, which refuses at construction);
the vector request's builders never validate reachability, its
execution does. The view materialization call itself carries no
deadline (the same index-less base path `execute.rs` records): the
entry checkpoint bounds entry, the later checkpoints everything after
materialization.

*Repair-loop-1 history:* the original landing materialized the default
only in the public accessor `VectorSearchRequest::deadline()` while the
checkpoints consumed the raw `Option` fields — an unbound request was
in fact an unbounded scan, and both gates caught it. The bound is now
materialized on the execution path itself and pinned by the tests named
in the repair-loop section.

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
- `tests/query/main.rs` (adopted with the base red commit `c598006`,
  +2 lines): registers `vector_conformance_vectors` and `vector_search`
  with the query integration harness. Within the packet's owned test
  scope; disclosed here because the loop-0 handoff omitted it from its
  files_changed list (inspector MINOR).
- `src/query/spec.rs`, `src/index/definitions.rs`: **not edited** — the
  field-name rule is consumed through the existing `pub(crate)`
  `validate_field_name` and the TQL identifier bound through the
  existing `pub MAX_FIELD_BYTES`; since repair loop 1 the index-name
  rule is consumed by delegating to the existing
  `pub StaticIndexName::try_new` (the loop-0 restatement of its rules is
  gone, and with it the drift risk the warden flagged); no edit to
  either shared file was required.

## Disclosed limits

- Execution materializes the snapshot's document list and scans it —
  the same index-less base path W3-2 records for ordinary queries. On a
  large collection an exact k-NN scores every candidate vector; the
  bounded checkpoints are the only guard. No claim about scan cost is
  made or measured here. The materialization call itself takes no
  deadline (D6): the entry checkpoint bounds entry, the later
  checkpoints everything after materialization.
- A stored vector field present with a **null** value is malformed (fail
  closed, `InvalidStoredVector`); only an **absent** field is a
  non-candidate. The tests pin the absent-field rule; the null-field
  rule is the implementation's documented strict reading.
- A stored vector field present as an **empty array** classifies as
  `DimensionMismatch { expected: d, actual: 0 }` — emptiness is read as
  a dimension fact — while null and nested-array values classify as
  `InvalidStoredVector` shape facts. Recorded verbatim in repair loop 1
  from the warden's hostile-input probes; behavior unchanged.
- The execution fails the whole search on the first dimension-mismatched
  or malformed stored vector of the source collection — fail-closed by
  design; it does not skip the offender.
- `VectorSearchMode::Approximate` exists as typed vocabulary with no
  producing path; `request.mode()` is always `Exact`.
- The similarity measure is cosine only (`VectorSimilarity::Cosine`);
  the reported score is clamped into [-1, 1] (D2's repair-loop-1
  clamp).

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

- Public seam (`tests/query/vector_search.rs`, 25 tests): declaration
  construction and validation incl. reserved namespace, family
  collection, and the whole-string (non-ASCII-refusing) static-index
  name rule; identity key determinism and content addressing; drift
  separation; dimension bounds at the competitive bar; limit bounds;
  empty/out-of-bounds/non-finite query vectors; threshold range; the
  request-side invalid-field refusal; filter composition; declaration
  binding; TQL render, versioning, lowering, its refusals, and the
  bare-identifier rule for its names; deadline/cancellation binding.
- Conformance corpus (`tests/query/vector_conformance_vectors.rs`,
  36 vectors, 3 tests): every accept and refusal class of the
  construction surface has a named vector; exact-count assertions pin
  the corpus size; refusal-variant checks fail when a class loses its
  last producing vector.
- In-crate execution (`src/query/vector_tests.rs`, 19 tests incl. the
  5-vector semantic corpus): the pinned cosine values (1, 0, −1,
  −√2⁻¹); the zero-magnitude rule; descending order; the identity
  tie-break determinism pin (reverse-committed ids, re-executed) plus
  the direct comparator pin that discriminates the tie-break; k
  truncation with accounting; pre-scoring filter; schema-optional
  missing fields; collection isolation; dimension mismatch; the three
  malformed-stored-vector shapes; threshold drop; cancellation;
  foreign-snapshot refusal; the document payload beside id and score;
  the default-window execution bound and the entry checkpoint; the
  clamped score range.
- Red-then-green: the red run is the previous builder's record (commit
  `c598006`: `cargo test` failing with exactly two `error[E0432]`
  unresolved imports, exit 101). Green: all 47 vector tests pass; the
  full workspace suite is green (see receipts). Repair loop 1's red and
  mutation runs are recorded in the repair-loop section.

## Repair loop 1 (2026-08-28)

The inspector returned NO-GO and the warden FAIL: one shared MAJOR (the
recorded D6 bound did not exist in code), one record-integrity MAJOR
(the check.sh receipt), and four actionable MINORs. Everything else —
the delivered contract, write scope, V1 citations, charter discipline —
was verified compliant by both gates and is unchanged. Commits:
`5e961d2` (behavior-preserving execution seam), `0b0ef5e` (red tests),
`47e717e` (fixes), plus this record.

### Findings → fixes

1. **MAJOR — D6's default one-minute window was never enforced**
   (inspector 1 + warden 1). `check_bounds` received the raw `Option`
   fields, so a request built without `.with_deadline(...)` — the
   default for every naive caller — executed with no wall-clock bound,
   while D6, the module rustdoc, and the handoff all asserted the
   bound; the materializing accessor was dead code on the execution
   path. **Fix:** `vector_search_within` (crate seam, `5e961d2`)
   materializes the bound once at entry — deadline defaulting into the
   injected window, cancellation defaulting to a fresh signal — and all
   checkpoints pass the materialized bound; a new entry checkpoint
   fires *before* the snapshot view is materialized. **Tests (red
   first):** `an_unbounded_request_executes_inside_the_default_window`
   (the window injected as 1ns through the crate seam — a const
   one-minute window cannot be expired on a wall clock inside a test)
   and `an_expired_bound_deadline_refuses_before_any_data_access` (an
   expired deadline now outranks a foreign-Engine snapshot). Red run at
   `0b0ef5e`: in-crate exit 101, both failed (the unbound scan ran to
   completion; the foreign snapshot answered `SnapshotEngineMismatch`).
   Green at `47e717e`: 19/19.
2. **MAJOR — irreproducible check.sh receipt** (inspector 2). The
   loop-0 receipt table recorded `TERRABASE_V1_ROOT=... ./scripts/check.sh
   | 0` while disclosing the citation audit "did not execute" — from a
   nested worktree the workspace-root candidates carry neither `specs/`
   nor `TerraBase/`, and check.sh exits 1 at that stage, so exit 0 and
   the disclosure cannot both be true. **Fix:** the receipt below
   records the corrected invocation (explicit
   `TERRASCALE_WORKSPACE_ROOT`, the warden's resolution); the audit
   executed and passed strict validation at the pin. The loop-0 line
   was defective as recorded; the loop-0 cargo receipts were
   independently re-verified green by the inspector.
3. **MINOR — `tests/query/main.rs` missing from the loop-0 handoff's
   files_changed** (inspector 3). Disclosure only: the +2-line module
   registration came with the adopted red commit `c598006`; it is now
   listed under Proposed collision actions.
4. **MINOR — the identity tie-break pin was non-discriminating**
   (warden 2). Deleting `.then_with(compare_identity)` left the whole
   vector suite green because the snapshot view already iterates
   documents in canonical key order. **Fix:** the sort comparator is
   extracted as `compare_candidates` (crate-visible) and pinned
   directly from a non-identity input order
   (`the_identity_tie_break_discriminates_tied_scores_from_any_order`).
   **Mutation proof:** deleting the `.then_with` clause → the new test
   fails (exit 101); restored → 19/19 green.
5. **MINOR — `TqlVectorPredicate` accepted grammar metacharacters**
   (warden 3). The field-name rule admitted commas, parentheses, and
   spaces, so field `"a, 0.9) or true("` rendered a second predicate
   and parameter `"p, 0.5) limit 999"` an injected threshold and limit
   line — contrary to the rustdoc's injection-safety claim. **Fix:**
   field and parameter must be bare TQL identifiers (ASCII letter or
   underscore first, then letters/digits/underscores, at most
   `MAX_FIELD_BYTES`); anything else refuses
   `VectorSearchError::InvalidField`, and the rustdoc states the actual
   rule (a future TQL-03 dotted-path form is an additive widening
   within `vec/1.0`'s minor line). **Tests (red first):**
   `tql_predicate_names_are_bare_identifiers_refusing_grammar_
   metacharacters` and corpus vectors `tql/name-field-metacharacters` /
   `tql/name-parameter-metacharacters`. Red run at `0b0ef5e`:
   `--test query` exit 101, both failed ("produced Ok(()) for
   RefusedRequest(InvalidField)"). Green at `47e717e`: 84/84.
6. **MINOR — request-side `InvalidField` had no test and no conformance
   vector** (warden 4). **Fix:** `an_invalid_request_field_refuses_typed`
   plus corpus vectors `request/field-empty` /
   `request/field-whitespace-padding`; `InvalidField` joined the
   refusal-variant completeness list. These are green-on-arrival (the
   refusal existed — the finding was the missing pin);
   **discrimination proved by mutation:** removing the
   `validate_field_name` call from `VectorSearchRequest::try_new` → the
   test fails (exit 101); restored → green (exit 0).
7. **NOTE — exact match could score one ulp past 1.0** (warden 5).
   **Fix:** finite scores clamp into [-1, 1] after the fail-closed
   finiteness check (non-finite still refuses `InvalidStoredVector`).
   **Test (red first):**
   `the_reported_score_stays_inside_the_documented_cosine_range` — red
   at `0b0ef5e` (reported 1.0000000000000002), green at `47e717e`.
8. **NOTE — `validate_index_name` duplicated
   `StaticIndexName::try_new`'s rules** (warden 7). **Fix:** it
   delegates to the static index language's own validator; behavior is
   unchanged (pinned by
   `declaration_names_reuse_the_static_index_name_rule_whole_string`:
   non-ASCII names refuse typed), and the two rule sets can no longer
   drift.
9. **NOTE — hostile-input classification details** (warden 6) are
   recorded verbatim as disclosed limits above (empty stored array =
   `DimensionMismatch { actual: 0 }`; the view materialization carries
   no deadline); behavior deliberately unchanged.

The inspector's NOTE on the TQL render wording (it covers the 8.2
predicate clause + limit line, not the `order by score() desc`
composition) is corrected in the "What landed" wording above; the
NOTE on lib-test count drift (885 claimed, 886 observed at loop-0
HEAD) is resolved by the per-target counts in the receipts below.

### Repair-loop test receipts

| Run | Exit |
| --- | --- |
| RED `cargo test --workspace --locked --lib -- query::vector` at `0b0ef5e` — 3 failed (default window, entry precedence, score range), 15 passed | 101 |
| RED `cargo test --workspace --locked --test query` at `0b0ef5e` — 2 failed (TQL identifiers: public test + corpus), 82 passed | 101 |
| MUTATION delete the `.then_with(compare_identity)` tie-break → tie-break pin test | 101 |
| MUTATION remove `validate_field_name` from `VectorSearchRequest::try_new` → InvalidField test | 101 |
| same, restored → InvalidField test | 0 |
| GREEN `cargo test --workspace --locked --lib -- query::vector` at `47e717e` — 19/19 | 0 |
| GREEN `cargo test --workspace --locked --test query` at `47e717e` — 84/84 | 0 |

## Receipts

All commands run inside the packet worktree
`.worktrees/terrabasev2-gapc4` on branch `fleet/gapc4-2`, after
`unset RUSTUP_TOOLCHAIN`:

| Command | Exit |
| --- | --- |
| `cargo fmt --all -- --check` | 0 |
| `cargo check --workspace --locked` | 0 |
| `cargo test --workspace --locked` (dev profile, REQUIRED) | 0 — every target green; `terrabase_v2` lib 889 passed + 1 ignored, `tests/query` 84 passed |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 |
| `TERRABASE_V1_ROOT=/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale ./scripts/check.sh` | 0 — includes the release-profile test pass (green), rustdoc, parity tooling, benchmark-receipt discipline, license check, secret scan, and the STRICT V1 citation audit (executed from this worktree and passed; see the correction below) |

**Receipt correction (repair loop 1, MAJOR 2):** the loop-0 table
recorded the check.sh line without `TERRASCALE_WORKSPACE_ROOT` and with
exit 0, alongside a disclosure that the citation audit "did not
execute" — from a nested worktree that invocation deterministically
exits 1 at the audit stage, so the recorded exit was wrong (or an
unrecorded env var made the "did not execute" statement wrong). The
corrected invocation above exports the workspace root explicitly; the
audit then executes and passes in strict mode at pin
`fa99b9257776dbe625e6a2388c74c9ce916637ee`. Any nested-worktree re-run
of check.sh needs `TERRASCALE_WORKSPACE_ROOT` set.
