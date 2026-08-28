# The analyzer and term-vector foundation (GAP-Q13)

Ticket: TerraScale/TerraBaseV2#67 (GAP-Q13, folding GAP-Q17's
engine-selection disposition per charter PD-2026-08-26-14 D3).
Implementation: `src/query/analyzer.rs` (public seam
`terrabase_v2::query::*`, reached through one export collision action
recorded below); focused tests: `tests/query/analyzer_foundation.rs`
(public path, 4 tests) and `src/query/analyzer.rs` in-crate (24 tests).
No parity overlay is mutated by this document or by the builder: every
row below stays `unresolved` until the parent applies a WS-PARITY
disposition from these proposals.

V1 source identity for every anchor below: commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`, read from the pinned clean
read-only worktree reached through `TERRABASE_V1_ROOT`. Citations use the
parity form `[v1:path::Symbol]`.

This is a design/decision record in the line of
`docs/indexing/static-index-definitions.md` (GAP-Q06), not an ADR: ADR
numbering is parent-owned. Disposition vocabulary as in that document:
**adopt**, **adapt**, **accepted-difference**. Decisions the parent must
ratify are labeled **PROPOSED**.

## What landed

A typed, bounded, code-free analysis foundation — and nothing that
searches:

- **The closed analyzer configuration set** (`AnalyzerKind`): V1's three
  engine-default analyzers resolved by their V1 names (`KeywordAnalyzer`,
  `LowerCaseKeywordAnalyzer`, `TerraBaseStandardAnalyzer`), plus one
  declarative composition form (a closed `TokenizerKind` plus an ordered,
  bounded `TokenFilterKind` list). Name resolution fails closed for
  everything else.
- **The versioned term-vector contract** (`TermVector`,
  `TermVectorEntry`, `TermOffset`, `TermVectorMode`): frequencies,
  positions, and byte offsets in the mode combinations V1's client
  contract defines, with total construction validation and an
  `analyzer/1.0` version identity that inbound vectors must carry exactly.
- **The analysis operation** (`Analyzer::analyze`): one field value under
  one configuration into one term vector, bounded at input, term, and
  token mass, one linear walk with no recursion, and no code anywhere on
  the path.
- **Tests** pinning every rule above, red-then-green on the public seam.

Explicitly **not** landed: no scoring, no match, no ranking, no query
predicate consumes an analyzer, no index reads or writes analyzed terms,
no highlighting/suggestions/more-like-this/facets, no analyzer registry
beyond name resolution, no storage of term vectors anywhere. The
relevance rows this packet gates stay `unresolved`.

## The decision

### D1 — One engine surface first: V2 builds one native search engine and no engine-selection surface (PROPOSED)

Recorded as evidence, not preference. The V1 facts at the pin:

- V1 ships **two** selectable engines. The client contract exposes the
  choice as a three-valued enum — `None`, `Corax`, `Lucene`
  ([v1:src/TerraBase.Client/Documents/Indexes/SearchEngineType.cs::SearchEngineType])
  — carried per index as `Indexing.Auto.SearchEngineType` and
  `Indexing.Static.SearchEngineType`
  ([v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration]).
- V1's **default for both auto and static indexes is Corax**
  ([v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration],
  constructor lines 40-41: `AutoIndexingEngineType =
  SearchEngineType.Corax; StaticIndexingEngineType = SearchEngineType.Corax;`).
- V1's Corax is **not analyzer-independent**: for every analyzer that is
  not one of the three native Corax compositions, Corax wraps a Lucene
  analyzer through an adapter
  ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxIndexingHelpers.cs::GetCoraxAnalyzer];
  [v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/LuceneAnalyzerAdapter.cs::LuceneAnalyzerAdapter]).
  V1's analyzer vocabulary is Lucene's even on the Corax path.
- The two engines are **observably different on the same query**: the
  Lucene path highlights through a fast-vector highlighter over stored
  term vectors
  ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/LuceneIndexReadOperation.cs::LuceneIndexReadOperation],
  `FastVectorHighlighter` at lines 63/404-409), while the Corax path
  highlights by case-insensitive substring match over the raw field text
  and splits fragments on spaces — it consumes no term vectors at all
  ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxIndexReadOperation.cs::ProcessHighlightings],
  lines 963-1030).

The V2 conclusion this packet records: **V2's first full-text search
surface is one native engine, and there is no engine-type selection
surface in it.** A second engine is not built, not selected, not
configured, and not promised. The observable-compatibility obligation a
second engine would create (same query, same relevance rows, same
answers across engines) does not exist while there is one engine, and is
deferred with this record until a second engine is ever proposed. The
typed analyzer foundation landed here is the engine-independent half —
it is the contract either engine would have to speak — which is exactly
why it lands first and alone.

**PROPOSED for parent ratification.** This is the GAP-Q17 disposition;
see the dedicated section below for the row-level action.

### D2 — Analyzers are typed declarative configurations, never code (PROPOSED; the charter has already ruled)

An analyzer is a closed typed value: three named configurations plus a
tokenizer/filter composition. There is no variant, field, or name in the
contract that can carry source code, and therefore nothing to compile,
load, or execute.

The rejected alternative is V1's actual model: a client-authored
analyzer is a `Name` plus **C# source code** (`Code`)
([v1:src/TerraBase.Client/Documents/Indexes/Analysis/AnalyzerDefinition.cs::AnalyzerDefinition],
with `Validate` requiring both fields at lines 27-34), and the server
compiles that source with Roslyn at deployment
([v1:src/TerraBase.Server/Documents/Indexes/Analysis/AnalyzerCompiler.cs::AnalyzerCompiler],
`CSharpCompilation`/`Emit` at lines 46-59). That execution model is
excluded by the charter's no-arbitrary-code rule — "No arbitrary code
execution in the language… projections are typed expressions"
(PD-2026-08-26-08 D2) — the same rule that replaced RQL's JavaScript
projections with typed TQL expressions and V1's JavaScript index maps
with the GAP-Q06 typed definition language. A scripting or compilation
runtime inside the engine would also contradict the sandbox rules this
packet is required to make explicit and testable. The contract risk row
flags exactly this: analyzer code "can execute or transform user data
and must be isolated and reproducible" (PB-4048, `parity/contracts/query.json`);
a closed typed set is isolated and reproducible by construction.

**PROPOSED for parent ratification** (as the recorded form of the rule;
the rule itself is the charter's).

### D3 — The term-vector contract: frequencies, positions, byte offsets, versioned (adopt, shapes; PROPOSED, bounds)

The shapes are V1's, adopted:

- The mode set is V1's client `FieldTermVector` verbatim — `No`, `Yes`
  (frequencies only), `WithPositions`, `WithOffsets`,
  `WithPositionsAndOffsets`
  ([v1:src/TerraBase.Client/Documents/Indexes/FieldTermVector.cs::FieldTermVector],
  lines 6-29). `Frequencies` is V1's `Yes` under the name that says what
  it carries; the wire names round-trip V1's enum member names.
- Per term, the vector carries the term, its frequency, and one position
  and one offset per occurrence. That is the exact shape V1's
  fast-vector highlighter consumes: a term → offsets map, a term →
  positions map, and a term → frequency map
  ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/Highlightings/FastVectorHightlighter/VectorHighlightMapper.cs::VectorHighlightMapper],
  lines 9-26).
- Positions are the per-entry term order — the positional "term vector"
  V1's engine keeps per entry for phrase matching
  ([v1:src/TerraBase.Corax/Querying/Matches/PharseMatch.cs::PhraseMatch],
  comment at lines 121-122).
- The field option that requires the fullest mode is V1's highlighting
  indexing mode: search analysis with positions **and** offsets tracked
  ([v1:src/TerraBase.Client/Documents/Indexes/FieldIndexing.cs::AutoFieldIndexing],
  `Highlighting` at lines 52-55).

The declarations that differ from V1, each deliberate:

- **Offsets are UTF-8 byte offsets into the original field value.** V1's
  token carries `Offset`/`Length` into the analyzer's (possibly
  transformed) buffer
  ([v1:src/TerraBase.Corax/Pipeline/Token.cs::Token], lines 5-13).
  Offsets into the original value are stable under filtering and are
  what a highlighter needs; this is a V2 declaration, not a V1
  equivalence claim.
- **Construction is total.** Mode agreement (an entry carries positions
  iff the mode carries them), strictly increasing positions, ordered
  non-overlapping offsets, one entry per distinct term in term order,
  and bounded frequency mass are all checked before a `TermVector`
  exists. V1's engine-side guard is an overflow bound of
  `int.MaxValue >> 1` terms per field
  ([v1:src/TerraBase.Corax/Constants.cs::Constants.IndexWriter],
  line 93; enforced at
  [v1:src/TerraBase.Corax/Indexing/IndexWriter.IndexEntryBuilder.cs::IndexEntryBuilder],
  lines 234-235) — an overflow guard, not a sandbox bound.
- **Versioned.** Every vector carries `analyzer/1.0`; inbound
  construction from any other version fails closed before any other
  validation runs, following the TQL grammar version and the `idx/`
  definition-language version pattern. Stored vectors, migration
  tooling, and the TBEP surface share this one identity.
- **An empty value produces an empty vector.** V1's keyword tokenizer
  emits a single zero-length token for an empty source
  ([v1:src/TerraBase.Corax/Pipeline/KeywordTokenizer.cs::KeywordTokenizer]);
  V2's term contract requires non-empty terms, so an empty value yields
  no terms. Accepted difference.
- **Bounds** (`MAX_ANALYZER_NAME_BYTES` 255, `MAX_TOKEN_FILTERS` 4,
  `MAX_ANALYZED_VALUE_BYTES` 1 MiB, `MAX_TERM_BYTES` 4096,
  `MAX_TOKENS_PER_FIELD` 8192) are initial engine values, tunable with
  measured evidence — the same standing note GAP-Q06 carries for its
  bounds. V1 sizes analyzer buffers from the input (4 KiB default,
  [v1:src/TerraBase.Corax/Constants.cs::Constants.Analyzers], line 163)
  and grows them; V2 refuses the input instead of growing without a
  ceiling.

**PROPOSED for parent ratification** on the bounds; the shapes are
adopted from V1's contract.

### D4 — The sandbox rules (adopt; tested)

1. **No arbitrary code** — closed typed values only (D2). Tested
   negatively: no code-carrying surface exists to test positively.
2. **Bounded tokenization** — input cap, term cap, and token-mass cap,
   each refusing with a typed error. Correction (integrator, from the
   warden finding): the token cap is checked AFTER `tokenize` has
   materialized the token ranges, not as tokens are emitted — so the
   intermediate allocation is bounded by the INPUT cap
   (`MAX_ANALYZED_VALUE_BYTES`), not by the token cap; a pathological
   value allocates at most proportional to its (already-bounded) input
   and then refuses. The original "checked as tokens are emitted / no
   allocation toward its length" wording overstated the mechanism and is
   withdrawn. All three caps are reachable in tests, and each cap's
   exact boundary is pinned as valid.
3. **No recursion** — tokenization is one linear walk. Tested with
   maximal pathological inputs (a full-cap run of separators; a
   full-cap run of one character).
4. **Unknown fails closed** — unknown analyzer name, unknown
   term-vector mode token, unknown contract version. V1 also refuses an
   unknown analyzer name
   ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/LuceneIndexingExtensions.cs::GetAnalyzerType],
   `throw new InvalidOperationException($"Cannot find analyzer type …")`
   at line 56) and an unknown indexing mode
   ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/AnalyzersScope.cs::AnalyzersScope.ThrowWhenAnalyzerModeNotFound],
   lines 147-149); V2 refuses with typed errors instead of exceptions,
   and never substitutes equality.

### D5 — The V1 analyzer-name compatibility set is the three defaults, nothing else (accepted-difference, PROPOSED)

`AnalyzerKind::resolve` accepts exactly V1's three engine-default
analyzer names — `LowerCaseKeywordAnalyzer` (general default),
`KeywordAnalyzer` (exact default), `TerraBaseStandardAnalyzer` (search
default)
([v1:src/TerraBase.Client/Constants.cs::Constants.Documents.Indexing.Analyzers],
lines 668/670/672) — and refuses every other name. V1's resolution
accepts far more: any type name found in the Lucene assembly, any
assembly-qualified or namespace-qualified type string, and any
compiled custom analyzer
([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/LuceneIndexingExtensions.cs::GetAnalyzerType],
lines 41-57).

This is an **accepted difference with a migration consequence**, not a
gap to close silently: a V1 index definition naming any other analyzer
has no V2 equivalent in this foundation. The rows and the migration path
are named below (deferred, owner: parent + GAP-Q6). Extending the set is
additive — each new analyzer is a new typed kind plus its parity row —
and requires no contract change, which is the property D2 buys.

## The GAP-Q17 disposition (PB-4052)

Row PB-4052 (`corax-lucene-search-engine-selection`, classification
`core`) requires an explicit disposition for the engine-selection
surface; the contract allows "V2 may implement one engine first only
with an explicit unresolved disposition" (row text and the sweep's
proposed disposition, `docs/parity/ps-02-query-sweep.md` PB-4052
section).

**Proposed WS-PARITY disposition: `accepted-difference` for the
selection surface, with the cross-engine obligation deferred, and the
row staying `unresolved` until the parent applies it.** Evidence:
D1 above — V1 ships two engines with Corax as the default of both index
kinds; V2 ships one engine surface with no selection surface; a
cross-engine observable-compatibility obligation cannot exist while
there is one engine, and the two V1 engines are already observably
different on highlighting (the term-vector-consumer versus the
substring-matcher), which is the exact risk the row guards. The deferral
is time-unbounded and owned by the parent: it ends only if a second
engine is ever proposed, at which point the obligation is a new row with
its own evidence, not a silent extension of this one.

## WS-PARITY proposals

Proposed dispositions only. The parent/integrator applies overlays; no
row is flipped by this packet. Every row's authoritative status stays
`unresolved`.

| Row | Proposal | Evidence and condition |
| --- | --- | --- |
| PB-4009 (phrase/fuzzy/proximity/boosting) | **keep `unresolved`; blocked-on-foundation → unblocked-for-decision** | The analyzer foundation now exists, so the blocking dependency is gone, but the row's semantics need a scoring/matching engine that does not exist. Phrase matching is position-driven — V1's engine keeps per-entry term order ([v1:src/TerraBase.Corax/Querying/Matches/PharseMatch.cs::PhraseMatch]) and this contract types it — so the foundation is necessary, not sufficient. The row forbids silent equality substitution; nothing here substitutes anything. |
| PB-4019 (facet aggregation) | **keep `unresolved`; no change from this packet** | Facets are aggregation-side (GAP-Q8) with an analyzer dependency only for term facets. No facet types landed here. The sweep's disposition (gated on GAP-Q13 and GAP-Q8) holds; this packet clears the GAP-Q13 half only. |
| PB-4020 (suggestions) | **keep `unresolved`; analyzer-compatibility record now exists** | The contract requires "an unsupported analyzer or suggestion mode is reported explicitly." V1's suggestion surface is analyzer- and distance-based (Levenshtein/JaroWinkler/NGram, accuracy, popularity sort, page size 15 — [v1:src/TerraBase.Client/Documents/Queries/Suggestions/SuggestionOptions.cs::SuggestionOptions]; engine side [v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxSuggestionIndexReader.cs::CoraxSuggestionReader]). Fail-closed analyzer resolution is landed and tested; the suggestion index and distance layer are not. |
| PB-4021 (more-like-this) | **keep `unresolved`; analyzer-assumption record now exists** | The contract requires an explicit analyzer-assumption disposition. V1's Corax path builds more-like-this from analyzed terms with a stop-words document and options ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxIndexReadOperation.cs::MoreLikeThis], lines 1107-1155). The term-vector contract types the input it needs; the feature is not implemented. |
| PB-4031 (query highlighting) | **keep `unresolved`; term-vector dependency now typed** | Hard dependency on PB-4047 term-vector modes and PB-4048 analyzers (sweep). The mode set is now typed and versioned (D3), and V1's own highlighting evidence is now recorded on both engines (D1) — including that V1's Corax highlighting does not consume term vectors at all, so "V1 text-fragment semantics" is engine-dependent in V1 itself. The parent should decide which engine's fragment semantics V2 targets before this row moves. |
| PB-4048 (analyzers and indexed suggestions) | **keep `unresolved`; foundation landed, row not satisfied** | This row's own semantics (named analyzers preserve tokenization/exactness/case/stopword behavior; definitions validate name and code before deployment; suggestion-enabled fields retain analyzer and index metadata) are not met: no stopword filter exists, no suggestion-indexed field option exists, and V1's `Code` field is deliberately absent (D2). Validation-before-deployment is partially landed: a name is validated and resolved before any analysis runs. Note V1's `AnalyzerDefinition.Validate` requires a `Code` field V2 will never have — the row's disposition must be an adaptation, not an adoption. |

## Rejected alternatives

- **R1 — Adopt V1's compiled custom analyzers (rejected).** V1 compiles
  C# analyzer source at deployment
  ([v1:src/TerraBase.Server/Documents/Indexes/Analysis/AnalyzerCompiler.cs::AnalyzerCompiler]).
  Arbitrary code in the analysis surface is excluded by charter
  PD-2026-08-26-08 D2, and a compilation runtime in the engine
  contradicts the bounded, deterministic, reproducible design of every
  other seam. The security-review downstream impact on PB-4048
  ("analyzer code can execute or transform user data and must be
  isolated and reproducible") is satisfied by construction here.
- **R2 — Embed an existing analyzer framework (Lucene.NET or a Rust
  Lucene/Tantivy port) as the analyzer source of truth (rejected for
  now).** V1's own Corax did effectively this — its analyzer vocabulary
  is Lucene's, adapted per call
  ([v1:src/TerraBase.Server/Documents/Indexes/Persistence/Corax/CoraxIndexingHelpers.cs::GetCoraxAnalyzer];
  [v1:src/TerraBase.Server/Documents/Indexes/Persistence/Lucene/LuceneAnalyzerAdapter.cs::LuceneAnalyzerAdapter]).
  Embedding a framework before V2 has an engine couples the contract to
  a dependency chosen for the wrong reason (familiarity, not evidence),
  and the exact tokenization semantics of a framework are exactly what
  the relevance rows need pinned by tests V2 owns. The typed contract
  here keeps the option open: adopting a framework later is an
  implementation of `TokenizerKind`/`TokenFilterKind`, not a contract
  change. Rejected now, revisit with evidence when an engine lands.
- **R3 — Untyped analyzer payloads (name → JSON/string configuration,
  rejected).** An open payload cannot fail closed by construction and
  carries unbounded, unvalidated content across a public seam. Every
  seam contract in this repository is typed for exactly this reason;
  GAP-Q06 rejected the same alternative for index definitions.
- **R4 — Ship the contract without the analysis operation (rejected).**
  A foundation whose bounds are declared but never executed cannot be
  tested, and the ticket requires the sandbox rules tested. The bounded
  tokenizer is the smallest operation that makes every bound and every
  fail-closed rule observable — and it is not a search engine: it
  matches, ranks, and stores nothing.
- **R5 — A second engine now (rejected).** No evidence demands one: V1
  defaults to Corax for both index kinds
  ([v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration],
  lines 40-41), and V1's own two engines disagree observably on
  highlighting. Building a selection surface before a second engine
  exists would add configuration V2 cannot test against anything.

## What this ticket does not claim

- **No search engine.** No predicate, scorer, ranker, or index path
  consumes an analyzer or a term vector. Nothing here can answer a
  query differently than before this packet landed.
- **No V1 tokenization parity for the `Standard` kind.** `Standard` is
  *declared* as maximal runs of Unicode alphanumeric characters. V1's
  standard tokenization is a SIMD implementation with its own boundary
  rules
  ([v1:src/TerraBase.Corax/Pipeline/Parsing/StandardTokenizers.cs::StandardTokenizers];
  the Lucene-side `TerraBaseStandardAnalyzer` is a third behavior).
  Token-boundary parity with either is unclaimed and untested — it is a
  deferred item below, and until it lands no relevance row may cite
  this foundation as its analyzer-parity evidence.
- **No stopword, stemming, n-gram, or ascii-folding filter.** V1 has
  them (e.g. the NGram analyzer with configured min/max gram,
  [v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration],
  `Indexing.Analyzers.NGram.*` at lines 287-294). The closed filter set
  is `LowerCase` only.
- **No term-vector storage.** The contract exists; no index field
  option consumes it and nothing persists it. PB-4047 (GAP-Q6's field
  options) is the consumer.
- **No parity overlay is mutated**; rows stay `unresolved` until
  WS-PARITY dispositions them from the proposals above.
- **No performance claim of any kind** (PD-2026-08-26-03). No benchmark
  ran; nothing here is characterized as fast or faster.
- The bounds are initial engine values, tunable with measured evidence.

## Test evidence

- Red-then-green on the public seam: `tests/query/analyzer_foundation.rs`
  was written and registered first and failed against the unexported
  module for exactly the right reason (`error[E0432]/[E0433]: no
  'AnalyzerKind'/'TermVectorMode' … in 'query'`, exit 101), then passed
  after the export collision action (exit 0).
- In-crate (`src/query/analyzer.rs`, 24 tests): the three V1 default
  names resolve; unknown names (including V1's assembly-qualified type
  spellings) fail closed; malformed names are refused as malformed
  before unknown; composite bounds and duplicate filters; identity
  round-trip; keyword single-term exactness; empty value → empty vector;
  whitespace positions and byte offsets over irregular spacing; standard
  punctuation splitting; lowercase merging case variants into one entry
  with ordered positions and original-text offsets; mode/carried-shape
  agreement; determinism; the input cap, term cap, and token cap each
  reachable with their exact boundaries valid; pathological full-cap
  inputs return; `No`-mode and invalid field refusal; V1 wire-name
  round-trip for all five modes; half-open bounded offsets; entry shape
  validation; version fail-closed before any other validation; mode
  agreement; ordered/duplicate-free entries; frequency-mass bound.
- Public path (4 tests): V1 default names + unknown fail-closed; the
  input and token bounds observable publicly; the versioned contract
  fails closed on other versions; V1 wire names round-trip.

## Receipts

Run in this worktree on branch `fleet/gapq13-2`, `unset RUSTUP_TOOLCHAIN`
before every cargo command (the host's ambient `RUSTUP_TOOLCHAIN=stable`
would otherwise override the pinned 1.97.1). Both the passing and the
failing runs are recorded; the failures are the author's iterations, not
host flakes.

| Command | Exit | Note |
| --- | --- | --- |
| `cargo test --locked --test query analyzer_foundation` (red, before the export collision action) | 101 | `error[E0432]/[E0433]`: the public seam could not see the module — the intended red |
| `cargo test --locked --lib query::analyzer` (first run) | 101 | two wrong test expectations (exact-analyzer case preserved; an inner-space field name is valid at this seam), fixed in the same pass |
| `cargo fmt --all -- --check` (first run) | 1 | formatting drift in the new files; `cargo fmt --all` applied, re-check 0 |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` (first run) | 101 | 23 findings, all mine: `[v1:…]` citation brackets in rustdoc (the house convention keeps V1 anchors in the record, not in rustdoc — none of the landed modules carry them), three redundant closures, two simplifiable boolean expressions, one truncating cast, one test-only import |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` (after fixes) | 0 | |
| `cargo check --workspace --locked` | 0 | |
| `cargo fmt --all -- --check` (after fixes) | 0 | |
| `cargo test --workspace --locked` | 0 | 1231 passed / 0 failed (dev profile; includes this packet's 24 in-crate and 4 public tests) |
| `cargo test --workspace --locked --release` | 0 | 1231 passed / 0 failed |
| `cargo doc --workspace --no-deps --locked` | 0 | 5 pre-existing `redundant explicit link target` warnings in other files (`crates/terrabase-tbep/src/server.rs`, `src/engine/mod.rs`, `src/families/hilo/mod.rs`, `src/query/includes.rs`, `src/query/mod.rs`); none in `src/query/analyzer.rs` |
| `TERRABASE_V1_ROOT=… ./scripts/check.sh` (first run) | 1 | failed at the V1 citation audit with `unable to resolve the TerraScale workspace root`: from a nested worktree (`TerraBaseV2/.worktrees/…`) neither `$repo_root/..` nor `$repo_root/../..` contains both `specs/` and `TerraBase/`. Environment artifact of the worktree layout, not a code failure; the script's own `TERRASCALE_WORKSPACE_ROOT` escape hatch resolves it. Every step before the audit had already passed (fmt, check, dev tests 1231/0, release tests 1231/0, clippy, doc, parity tooling, license, secret scan) |
| `TERRABASE_V1_ROOT=… TERRASCALE_WORKSPACE_ROOT=… ./scripts/check.sh` (second run) | 101 | one failure, in a pre-existing timing test this packet does not touch: `benchmarks::tests::a_self_comparison_reads_as_parity` (`src/benchmarks/tests.rs:486`) read a self-comparison as `Better` instead of `Stable` while the host ran the concurrent fleet's builds. Re-run in isolation three times: 3 × passed |
| `cargo test --locked --lib benchmarks::tests::a_self_comparison_reads_as_parity` (3 runs) | 0 | 3 × `1 passed; 0 failed` — the second-run failure is a load flake, not a regression |
| `TERRABASE_V1_ROOT=… TERRASCALE_WORKSPACE_ROOT=…/TerraScale ./scripts/check.sh` (third run) | 0 | full suite green: 2462 tests passed / 0 failed across dev and release, clippy 0, doc 0, parity validation and tooling, strict V1 citation audit (258 rows, 267 source citations, 0 unresolved gaps), license, secret scan. `cargo-deny` unavailable on this host, recorded by the script as unrun |

## Deferred items

| Item | Owner | Condition |
| --- | --- | --- |
| `Standard` token-boundary parity with V1's tokenizers (Corax SIMD and Lucene `TerraBaseStandardAnalyzer`) | TQL/query wave with the relevance rows | Needs an engine to differ observably; until then no relevance row may cite this foundation as analyzer-parity evidence |
| Stopword filter (V1's Lucene English stop-word set is package-internal, not citable at the pin) | engine/query | Extract and pin the list with tests before any `Search` predicate lands |
| Stemming, n-gram, ascii-folding filters | engine/query | Additive `TokenFilterKind` variants with their own rows |
| Wiring `TermVectorMode` into index field options (PB-4047) | GAP-Q6 follow-on / engine/index | The field-options type (`src/index/definitions.rs`) has no term-vector option today; this contract is its typed vocabulary |
| Lowering a definition's `AnalyzerName` through `AnalyzerKind::resolve` at registration | engine/index (IX wave) | `src/index/definitions.rs` validates the name's shape; resolution against the closed set is not yet enforced there |
| Extending the analyzer set beyond the three V1 defaults | parent + engine/query | Each addition is an accepted-difference decision with its own row and migration note |
| Term-vector persistence and the highlighting/suggestions/more-like-this/facet evaluators | engine/query + engine/index | The rows above; nothing here implements them |
| A second search engine, if ever | parent | Ends the D1/Q17 deferral and opens a new compatibility row |

## Proposed collision actions

One collision action, committed separately as `collision: export the
GAP-Q13 analyzer foundation and register its public test`
(`src/query/mod.rs`, `tests/query/main.rs`,
`tests/query/analyzer_foundation.rs`):

- `src/query/mod.rs` is outside this packet's write scope, but the
  analyzer root was wired in as a **private** declaration-only module:
  without a `pub use`, the typed contract is unreachable from the public
  seam (the red receipt above) and rustc treats its contents as dead
  code, so the packet cannot land warning-clean. The edit is one export
  block with a comment marking it the parent's to ratify or reshape.
- `tests/query/main.rs` gains the one `mod analyzer_foundation;`
  declaration the new test file needs to compile; the test file itself
  is in this packet's write scope.

No other file outside the write scope was touched. `src/lib.rs`,
`src/types.rs`, `src/operations.rs`, `Cargo.toml`, `Cargo.lock`, CI
files, and `parity/**` are unchanged.

## Disclosed limits

- The bounds are first values with no measured evidence behind their
  magnitudes; they are constants, so tuning them is a one-line change
  plus the boundary tests that pin them.
- The `Standard` tokenizer's boundary behavior is a V2 declaration
  (see the non-claims); any consumer relying on it for V1 parity is
  relying on an unverified behavior.
- `AnalyzerKind::resolve` answers names, not configurations, for the
  three defaults; a V1 definition naming another analyzer fails closed
  with `UnknownAnalyzer` and no mapping guidance — the migration path is
  deliberately not invented by this packet.
- Only this worktree was exercised. No release build ran outside
  `scripts/check.sh`; no benchmark ran; no multi-member or
  cross-engine scenario exists to run.
