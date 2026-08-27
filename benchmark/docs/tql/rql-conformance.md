# RQL-to-TQL parity conformance record

Status: conformance corpus and findings authored by ticket TQL-04 (wave
`2026-08-26-terrabase-v2-tql-tickets.md`, charter PD-2026-08-26-08 D3, ADR
0009). This record proves the mapping proposed in
[`docs/tql/rql-mapping.md`](rql-mapping.md) with executable conformance
vectors, states honestly which mapped constructs the current seam cannot
plan, and hands parity-row disposition proposals to WS-PARITY through
[`docs/parity/tql04-ws-parity-proposals.md`](../parity/tql04-ws-parity-proposals.md).
Date: 2026-08-26. This record mutates no parity overlay: every row stays
`unresolved` until WS-PARITY and the parent dispose.

## 1. What is proven, and how

Two committed artifacts carry the proof, both re-deriving their counts from
their own tables:

- **The parity corpus** —
  `tests/query/tql_rql_parity_vectors.rs`: one vector per row of the
  mapping document (85 rows, ids R01..R85 in document order; 85 vectors;
  the corpus test fails when any row lacks a vector or when the
  disposition counts drift from 42 direct, 14 translated, 29
  accepted-difference). Each vector carries the V1 RQL text, the mapped
  TQL text, and the outcome class the TQL side must produce today: it
  either **lowers** onto the W3-2 seam, **fails closed at planning**
  (`UnsupportedForPlanning` naming the construct), is **capability-gated**
  (`CapabilityUnavailable` without the capability), or is **rejected**
  (grammar or closed-registry fail-closed).
- **The execution corpus** — `src/query/tql/parity_tests.rs`: 13
  execution vectors over one shared five-document corpus. Every vector
  runs its TQL text through the one compile and execute seam and asserts
  two things: the result equals the equivalent hand-built typed
  specification's result (the seam's execution-parity rule), and the
  rows are exactly the pinned expected identities. A third test pins
  the shared corpus's observable distinctions (present null, present
  string, absent field, array-topped fields, identifier-alphabet names)
  so no vector silently loses its evidence data.

The V1 RQL text in both corpora is **pinned evidence, not executed
evidence**: it records the V1 spelling of each capability against the
pinned V1 source identity `fa99b9257776dbe625e6a2388c74c9ce916637ee`
(the sources cited row by row in `docs/tql/rql-mapping.md`). V1 is a C#
repository; nothing in this repository's CI runs it. The expected results
each execution vector pins are the contract both sides of the mapping are
claimed to produce over the shared data; CI proves the TQL side produces
them. Equivalence claims below are therefore claims about the TQL side's
executed behavior plus the mapping's recorded semantics — never a claim
that V1 was executed here.

The clause-order difference (RQL `from`-first, TQL `select`-first) is a
global translation exercised by every vector, exactly as the mapping
document records; no row is spent on it.

## 2. Conformance outcomes by mapping row

Outcome vocabulary: **lowers-and-executes** (the public vector lowers and
an execution vector proves equivalent results), **lowers** (the TQL text
lowers; no execution vector pins it — the shape is already covered by the
TQL-02 execution-parity tests for the same seam form), **not-planned(**
construct**)** (parses; the seam fails closed naming the construct —
seam-extension work, not a language gap), **gated(**
function**)** (grammar-gated on an engine capability), **rejected(**
class**)** (accepted-difference surface, fail-closed by design).

| Rows | Mapping (mapping-document section) | Outcome |
| --- | --- | --- |
| R01, R28 | `from <collection>`; whole document (1, 5) | lowers-and-executes |
| R02, R03 | aliased source; bare-alias translation (1) | lowers-and-executes |
| R05 | `from @all_docs` (1) | not-planned(`@all_docs`) |
| R04 | `from index` (1) | rejected(`ParseError`) |
| R06 | `= != <> < <= > >=` (2) | lowers-and-executes |
| R07 | `==` → `=` (2) | lowers-and-executes |
| R08 | `and` / `or` (2) | `and`: lowers-and-executes; `or`: not-planned(`or`) |
| R09, R10 | `not`; combined `and not`/`or not` (2) | not-planned(`not`) |
| R11 | parenthesized expressions (2) | lowers-and-executes |
| R12 | `between` (2) | lowers (GAP-EVAL seam extension) |
| R13, R14 | `in`; `all in` (2) | lowers (GAP-EVAL seam extension) |
| R15 | `true`/`false`/`null` literals (2) | lowers-and-executes |
| R16, R17 | string escapes; long/double literals (3) | lowers-and-executes |
| R18, R19, R20 | `$name` parameters; studio prefix; JSON body (3) | lowers-and-executes |
| R21 | `//` and `/* */` comments (3) | lowers-and-executes |
| R22 | identifier alphabet (3) | hyphenated: lowers-and-executes; numeric-leading: **see finding F1** |
| R23 | `@`-metadata paths → pseudo-fields (3) | not-planned(`metadata`) |
| R24, R25 | dot paths; array positional (4) | not-planned(`path`) |
| R26 | implicit any-element array match (4) | lowers; **see finding F2** |
| R27, R35 | `id()` (4, 6) | not-planned(`call`) |
| R29, R30 | `select field as alias`; bare alias (5) | not-planned(`alias`) |
| R31 | `select distinct` (5) | not-planned(`distinct`) |
| R32 | JS projection → typed constructors (5) | not-planned(`object constructor`) |
| R33, R84 | `declare function` (5, 11) | rejected(`ParseError`) — flagship |
| R34 | load-alias projections (5) | computed half not-planned(`object constructor`); the load binding itself lowers (GAP-EVAL) |
| R36 | `exists` (6) | lowers-and-executes |
| R37, R38 | `startsWith`/`endsWith` → snake_case (6) | lowers (GAP-EVAL seam extension) |
| R39 | `exact` (6) | not-planned(`call`) |
| R40, R41, R72 | `search`; `boost`; `score()` (6, 8) | gated |
| R61, R62 | `vector.search`; embedding bind (6) | gated |
| R63, R64, R65, R73 | spatial predicates, shapes, distance (6, 8) | gated |
| R57 | `when(…)` → `if(…)` (6) | not-planned(`object constructor`) |
| R60 | `count`/`sum`/`avg`/`min`/`max` (6) | not-planned(`group by`) |
| R66 | `group by key` (7) | not-planned(`group by`) |
| R68 | `asc`/`desc`/`ascending`/`descending` (8) | lowers-and-executes |
| R69 | `nulls first`/`nulls last` (8) | lowers-and-executes |
| R75, R76, R77 | `limit n`; comma form; `offset` (9) | lowers-and-executes |
| R80 | `load path as alias` (10) | the clause lowers with alias-qualified projections (GAP-EVAL); a `where` over the loaded alias stays not-planned(`path`) |
| R81 | `include path` (10) | not-planned(`include`) |
| R42–R56, R58, R59 (registry rejections) | `regex`, `lucene`, `fuzzy`, `proximity`, `moreLikeThis`, `intersect`, `cmpxchg`, `counters`, `revisions`, `array`, `highlight`, `explanations`, `timings`, `now`, `today`, `timeseries`, `last` (6) | rejected(`UnknownFunction`), except `last`: rejected(`ParseError`) — `last` is a reserved word |
| R67 | `group by … with <method>` (7) | rejected(`ParseError`) |
| R70, R71, R74 | order type hints; `random()`; custom sorter (8) | rejected(`ParseError`/`UnknownFunction`) |
| R78, R79 | `filter`; `filter_limit` (9) | rejected(`ParseError`) |
| R82 | family includes (10) | rejected(`ParseError`) |
| R83 | `update { … }` (11) | rejected(`ParseError`) |

Corpus counts, re-derived from the committed vector table (85 vectors):
24 lower, 24 not-planned, 8 gated, 29 rejected. Of the 23 distinct rows
whose vectors lower, 19 are pinned equivalent through execution by the 13
execution vectors (R01, R02, R03, R06, R07, R15, R17, R18, R21, R22,
R26, R28, R29, R36, R68, R69, R75, R76, R77). The remaining four (R08
`and`, R11 parentheses, R16 string escapes, R19/R20 parameter-map
translations) lower onto the identical seam forms — conjunctions,
comparisons, bound parameters — that those execution vectors and the
TQL-02 execution-parity suite already pin; their public-corpus vectors
prove the lowering.

## 3. Findings (recorded, not silently repaired)

**TQL-04-F1 — numeric-leading bare identifiers do not parse.** The
specification (Section 3.2) and the mapping row R22 promise that a bare
identifier may start with a digit. The lexer always routes a leading digit
to `scan_number`, so `2024batch` lexes as `Int(2024)` followed by the
identifier `batch` and the query fails closed with `ParseError` — the
fail-closed outcome, but not the specified one. The working address is the
quoted spelling `"2024batch"`, which the corpus pins as the operative
translation. This is a parser-implementation gap against the accepted
specification, discovered by the corpus; per the ticket's rules it is
recorded here rather than repaired by extending the grammar. Vectors:
`identifier-alphabet-numeric-leading` (quoted form lowers and executes)
and `identifier-alphabet-numeric-leading-bare-fails-today` (bare form
pinned failing closed).

**TQL-04-F2 — the implicit any-element rule is not realized through the
current seam.** The language rule (spec Section 6.2; mapping row R26,
disposition direct) is that a predicate over an array field is true when
any element satisfies it. A lowered top-level comparison today compares
the field value structurally: over `tags = 'red'` with `tags` an array,
the current plan matches nothing — and, worse than fail-closed, it is not
fail-closed at all, because the lowering cannot know statically that the
field is array-topped. The corpus pins the current observable behavior
(`implicit-array-match-today-structural` expects zero rows; the
array-valued parameter vector pins the structural match that does hold),
and the TQL-vs-typed-specification equivalence still holds — both sides
execute the same seam semantics. Realizing the language rule (or failing
closed when the data shape disagrees) is seam-extension work for the seam
owner; the mapping row's `direct` disposition at the language level
stands, with this record as its honest execution-level caveat.

**TQL-04-F3 — the translated surface is planning-blocked, not
grammar-blocked.** Every `not-planned` outcome above is a construct the
`tql/1.0` grammar accepts and the static checks pass; lowering fails
closed with the construct named. The blocked constructs, as one list for
the seam owner: `@all_docs`, `or`, `not`, `between`, `in` (both
quantifiers), metadata paths, nested and array-navigated paths, calls
other than `exists`/`missing`, projection aliases, `distinct`, object and
array constructors, `group by` and the aggregates, `load`, and `include`.
This is exactly the seam-extension list ADR 0009 anticipated; no row's
mapping disposition changes because of it, but no translated mapping can
be claimed *executed-equivalent* until its construct leaves this list.
The WS-PARITY handoff below says so per row.

**TQL-04-F4 — gated predicates hold their text stable.** The eight gated
vectors prove both halves of ADR 0009 D-6: without the capability the
exact query text fails closed with `CapabilityUnavailable` naming the
function; with the capability reported, the text parses and then fails
closed at planning like its non-gated peers. No gated predicate degrades
to another predicate or a no-op. The capability evidence itself (search,
vector, spatial engines) is not part of this ticket and remains with the
owning waves (CP-01 for the required vector row).

## 4. Deliberate divergences (for parent review)

The mapping document's accepted-difference rows are the divergence list;
the corpus adds the fail-closed proof for each one that is a language
surface (29 rejection vectors). The two flagship divergences, restated
for the parent's review per the ticket's acceptance criteria:

1. **No JavaScript projections** (`select { … }` bodies, `declare
   function`): replaced by typed expressions and constructors (ADR 0009
   D-4; charter PD-2026-08-26-08 D2). The corpus proves both directions:
   the JS callback form fails closed (`js-callback-rejected`), and the
   typed replacement parses and statically checks
   (`js-projection-typed-constructor-not-planned` — execution awaits the
   constructor seam work of finding TQL-04-F3).
2. **No wall-clock, no randomness, no raw engine passthrough** (`now`,
   `today`, `random()`, `lucene`, index-name targeting): each rejected
   fail-closed with its recorded reason; deterministic replay
   (PD-2026-08-26-08 D4) and automatic indexing (PD-2026-08-26-09)
   require them absent.

## 5. Handoff

- **WS-PARITY**: disposition proposals for the query-family rows
  (PB-4000s) live in
  [`docs/parity/tql04-ws-parity-proposals.md`](../parity/tql04-ws-parity-proposals.md).
  No overlay is mutated by this ticket.
- **Seam owner** (finding F3): the not-planned construct list, in the
  order the corpus names them.
- **Parser owner** (finding F1): the numeric-leading identifier gap
  against spec Section 3.2 — lexer-first routing, specification promises
  identifier-first.
- **TQL-03**: the execution corpus's shared documents and pinned row
  identities are reusable as CLI/host/studio fixture expectations when
  the surface wave runs the same texts end to end.
