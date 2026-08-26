# V1 RQL to TQL capability mapping

Status: proposed by ticket TQL-01 (wave
`2026-08-26-terrabase-v2-tql-tickets.md`) under charter
PD-2026-08-26-08 D3. This document proposes dispositions only — it
mutates no parity row. TQL-04 turns every `translated` and `direct`
row into a conformance vector and every `accepted-difference` row
into a WS-PARITY disposition proposal for the parent to accept.
Date: 2026-08-26.

TQL-04 update (2026-08-26): the handoff is delivered. Every row of this
document (R01..R85) carries a conformance vector; the outcomes, the
execution-equivalence proof for the seam-expressible subset, and the
recorded findings live in
[`docs/tql/rql-conformance.md`](rql-conformance.md), and the WS-PARITY
disposition proposals in
[`docs/parity/tql04-ws-parity-proposals.md`](../parity/tql04-ws-parity-proposals.md).
This mapping's dispositions are unchanged by that work.

Disposition legend:

- **direct** — the capability exists in TQL with the same surface and
  semantics (module the clause-order difference); TQL-04 still proves
  it with a conformance vector.
- **translated** — the capability exists in TQL with a different
  surface; TQL-04 proves each with an RQL-text / TQL-text pair that
  must execute equivalently.
- **accepted-difference** — deliberately not carried into `tql/1.0`;
  the reason is recorded here and becomes a WS-PARITY disposition
  proposal.

The flagship difference, called out by the charter, is the
**JavaScript-to-typed-expression** pair: `select { … }` projections
and `declare function` (accepted-difference/translated respectively)
are replaced by TQL's typed expression system — no arbitrary code
execution in the language.

## Evidence base (pinned V1 source, read-only)

- **Parser** — `src/TerraBase.Server/Documents/Queries/Parser/QueryParser.cs`:
  operator set (`>=`, `<=`, `<>`, `<`, `>`, `==`, `=`, `!=`,
  `BETWEEN`, `IN`, `ALL IN`), order-by options (`ASC`, `DESC`,
  `ASCENDING`, `DESCENDING`, `as string|long|double|alphaNumeric`,
  `NULLS FIRST|LAST`), clause keyword scanning.
- **Scanner** — `src/TerraBase.Server/Documents/Queries/Parser/QueryScanner.cs`:
  identifier alphabet (letters, digits, `_`, `-`; `@`-rooted names),
  `//` and `/* */` comments.
- **AST** — `src/TerraBase.Server/Documents/Queries/AST/`:
  `OperatorType.cs`, `ValueTokenType.cs` (Parameter, Long, Double,
  String, True, False, Null), `OrderByFieldType.cs` (Implicit,
  String, Long, Double, AlphaNumeric, Random, Score, Distance,
  Custom), `FromClause.cs` (From, Alias, Index, Filter),
  `NegatedExpresson.cs`.
- **Method registry** —
  `src/TerraBase.Server/Documents/Queries/QueryMethod.cs`: the
  complete function-name list mapped in Section 6 below.
- **Grammar (studio)** —
  `src/TerraBase.Studio/languageService/grammar/BaseRqlParser.g4`
  and `BaseRqlLexer.g4`: clause inventory and ordering
  (`from … group by … where … load … update … filter … select …
  include … limit`), `@all_docs`, alias forms, JS projection and
  `declare function` forms, time-series sub-grammar, JSON parameter
  body.
- **Metadata** — `src/TerraBase.Server/Documents/Queries/QueryMetadata.cs`:
  `id()` handling, `spatial.distance` order-by argument rule,
  `filter`/`vector.search` exclusivity.

The grammar-level clause-order difference (RQL is `from`-first; TQL
is `select`-first with `from` mandatory and `select` optional) is a
global translation, not a row: every TQL-04 vector exercises it.

## 1. Query shape (`from`)

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `from <collection>` | `from <collection>` | direct | Collection-targeted `from` carried over unchanged. |
| `from <collection> as alias` | `from <collection> as alias` | direct | Identical surface. |
| `from <collection> alias` (no `as`) | write `from <collection> as alias` | translated | TQL requires `as`: unambiguous grammar, SQL-familiar; one rule for every alias position. |
| `from index 'Index/Name'` | — none | accepted-difference | PD-2026-08-26-09: automatic indexing selects indexes per query; naming an index is not a language concern in V2. Explicit indexes still exist; queries never target them by name. Revisit only with IX-wave evidence. |
| `from @all_docs` | `from @all_docs` | direct | Same token, same meaning (filter with `@collection`). |

## 2. `where` operators (Parser line 16; AST `OperatorType`)

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `=`, `!=`, `<>`, `<`, `<=`, `>`, `>=` | same operators | direct | Both not-equal spellings kept. |
| `==` | write `=` | translated | One equality operator; SQL-familiar, no dual surface to test. |
| `and`, `or` | `and`, `or` | direct | Same semantics, same precedence family. |
| `not` | `not` | direct | First-class negation (`NegatedExpresson.cs`). |
| `and not x` / `or not x` combined forms | write `and not x` / `or not x` | translated | TQL composes them from `and`/`or` + `not`; no special combined tokens. |
| parenthesized expressions | same | direct | Grouping carried over. |
| `x between a and b` | same | direct | Same shape; inner-`and` binding documented in the TQL grammar. |
| `x in (a, b, c)` | same | direct | Same shape, including the empty-list edge (V1 allows; TQL allows — predicate false). |
| `x all in (a, b)` | same | direct | Same quantifier semantics over array fields. |
| `true`, `false`, `null` literals | same | direct | Same three static values (`ValueTokenType`). |

## 3. Values, parameters, lexical rules

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| Single-quoted strings | same | direct | TQL additionally defines `''` escaping explicitly; TQL-04 vectors pin any escape difference. |
| Long and double literals | unsigned decimal literals (sign is unary) | direct | Same value domain. |
| `$name` parameters, values supplied out-of-band (query-parameters JSON) | `$name` parameters, typed out-of-band binding on the TQL operation | direct | Same separation of text and data; TQL adds strict pairing (`UnboundParameter` / `UnexpectedParameter`) so parameters can never inject text. |
| Studio parameter declaration prefix (`name = value` before the query) | operation-level typed parameter map | translated | Studio/CLI/MCP surfaces bind the same map; the prefix syntax is not part of the language. |
| JSON body suffix (studio grammar `json` rule) | typed parameter map | translated | Same data, carried by the typed operation instead of appended JSON. |
| `//` and `/* */` comments | same | direct | Scanner behavior preserved; comments are not part of the parsed artifact. |
| Identifier alphabet letters/digits/`_`/`-` | same bare alphabet | direct | Keeps V1's caller-chosen field names (including numeric-leading). |
| `@`-rooted identifiers, `@metadata.` paths | `@id`, `@collection`, `@last_modified`, `@change_vector`, `@metadata.<member>` pseudo-fields | translated | Same information, exposed as a typed closed set instead of open `@`-paths; `@all_docs` stays a source token. |
| — (not in RQL) | quoted identifiers `"…"` for dynamic fields | TQL addition | New capability, no parity row; addresses any legal stored name. |

## 4. Paths and documents

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| Dot paths `a.b.c` | same | direct | Document-native core carried over. |
| Array positional `a[0]` | same | direct | Same navigation. |
| Implicit any-element array matching (`tags = 'red'` over an array) | same default any-match semantics | direct | V1 semantics preserved; TQL-04 vectors pin it. |
| — (not in RQL) | explicit `a[]` any-element navigation | TQL addition | Makes the implicit rule addressable in paths and projections. |
| `id()` / `id(alias)` | `id()` / `id(alias)`, plus `@id` | direct | Same function; `@id` is the pseudo-field spelling. |

## 5. Projection and `select`

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `select *` | `select *` (and omitted `select`) | direct | Whole-document projection. |
| `select field (as alias), …` | same | direct | Field lists with `as` aliases. |
| `select field alias` (bare alias) | write `as alias` | translated | Same rule as `from` aliases. |
| `select distinct …` / `select distinct *` | `select distinct …` | direct | Same semantics (deduplicate after projection, before paging). |
| `select { <JavaScript> }` | typed object/array constructors `{ key: expr, … }` over the closed function registry | translated — **flagship** | The charter's deliberate replacement: the same shaping power as typed data, no code execution, statically checked, version-stamped. TQL-04 vectors cover the common RQL projection patterns (renaming, arithmetic, conditionals, nested shapes). |
| `declare function f(…) { <JavaScript> }` | — none | accepted-difference — **flagship** | No arbitrary code execution in the language. What JS functions computed is expressed with typed expressions (`if`, `coalesce`, arithmetic, constructors) or stays a typed engine operation. Security posture and versioned-AST determinism require this. |
| JS projections referencing `load` aliases | typed constructors over `load` aliases | translated | Same capability, typed form. |

## 6. Function registry (`QueryMethod.cs`, complete)

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `id` | `id([alias])` | direct | Document identity. |
| `exists` | `exists(field)` | direct | Already the W3-2 seam's condition set; TQL adds `missing()` as the negation spelling (TQL addition). |
| `startsWith` | `starts_with(s, prefix)` | translated | Same predicate, snake_case registry naming. |
| `endsWith` | `ends_with(s, suffix)` | translated | Same. |
| `exact` | `exact(comparison)` | direct | Same exact-match modifier. |
| `search` | `search(field, term [, and\|or])` | direct, gated | Same function shape; gated on the shipped full-text capability. |
| `boost` | `boost(predicate, weight)` | direct, gated | Same; rides with search ranking. |
| `regex` | — not in tql/1.0 | accepted-difference | Bounded, denial-safe regex evaluation must be proven before the registry admits it; planned as an additive minor bump. |
| `lucene` | — none | accepted-difference | Raw engine-query passthrough leaks engine internals and conflicts with automatic index selection. |
| `fuzzy` | — not in tql/1.0 | accepted-difference | Search tuning; returns with the search capability as registry options. |
| `proximity` | — not in tql/1.0 | accepted-difference | Same. |
| `moreLikeThis` | — not in tql/1.0 | accepted-difference | Revisit with the shipped search capability. |
| `intersect` | — none | accepted-difference | Fanout is an optimizer concern under automatic indexing, not a language concept. |
| `cmpxchg` | — none | accepted-difference | Cluster-wide compare-exchange is not part of V2's shipped topology surface; revisits with the replication/sync waves. |
| `counters` | — not in tql/1.0 | accepted-difference | Family-gated: counters parity is PB-3010. |
| `revisions` | — not in tql/1.0 | accepted-difference | Revisions capability not shipped in V2 yet. |
| `array` (array group projection) | — not in tql/1.0 | accepted-difference | Group-by array semantics revisit with the aggregation/IX evidence. |
| `highlight` | — not in tql/1.0 | accepted-difference | Returns with shipped search diagnostics, as a registry addition. |
| `explanations` | — not in tql/1.0 | accepted-difference | Query diagnostics are operation-level output in V2, not language constructs. |
| `timings` | — not in tql/1.0 | accepted-difference | Same. |
| `now` | — none | accepted-difference | Wall-clock dependence breaks deterministic replay of versioned artifacts and live results; pass time as a parameter. |
| `today` | — none | accepted-difference | Same. |
| `when` (time-series conditional) | `if(cond, then, else)` | translated | The typed replacement for the conditional form. |
| `timeseries` | — not in tql/1.0 | accepted-difference | Family-gated: time-series parity is PB-3020. |
| `last` (time-series range) | — not in tql/1.0 | accepted-difference | Same family gate. |
| `count`, `sum`, `avg`, `min`, `max` | same names, aggregate placement rules | direct | Same aggregates under `group by`. |
| `vector.search` | `vector.search(field, vector [, threshold])` | direct, gated | Same predicate; gated on the shipped vector capability (CP-01's required row). |
| `embedding.text`, `embedding.text.i8`, `embedding.text.i1`, `embedding.f32`, `embedding.f32.i8`, `embedding.f32.i1`, `embedding.i8`, `embedding.i1` | bind vectors as typed `$parameters` (or `vector.of(…)` inline) | translated | Vectors are data: the query never references an AI task or generates embeddings in-text. Embedding production is an engine/Platform capability; its output binds by parameter. Keeps text stable, deterministic, and free of AI-task identifiers. |
| `spatial.within`, `spatial.contains`, `spatial.disjoint`, `spatial.intersects` | same names | direct, gated | Same predicates, gated on the shipped spatial capability. |
| `spatial.circle`, `spatial.wkt`, `spatial.point` | same names | direct, gated | Same shape constructors. |
| `spatial.distance` (order by / projection) | same | direct, gated | Same use, same 2–3 argument rule noted in V1 `QueryMetadata.cs`. |

## 7. `group by`

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `group by key (as alias), …` | same | direct | Group keys with aliases; aggregates in `select`/`order by`. |
| `group by … with <method>` (custom grouping) | — not in tql/1.0 | accepted-difference | Custom grouping functions are code-adjacent; revisit with typed registry evidence of need. |

## 8. `order by` (Parser lines 19–21; AST `OrderByFieldType`)

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `asc` / `desc` / `ascending` / `descending` | same four spellings | direct | All four accepted. |
| `nulls first` / `nulls last` | same | direct | Explicit null placement. |
| `as string\|long\|double\|alphaNumeric` type hints | — none | accepted-difference | V2 orders by the engine's total document order (W3-2) with exact cross-type numeric comparison; coercion hints have no work to do. Revisit if parity evidence shows a behavioral difference that matters. |
| `random()` | — none | accepted-difference | Nondeterministic ordering breaks replayable live results and golden vectors. |
| `score()` | `score()` | direct, gated | Ranking rides with the shipped search/vector capability. |
| `spatial.distance(a, b)` | same | direct, gated | Same. |
| Custom sorter name (`OrderByFieldType.Custom`) | — none | accepted-difference | Named sorters are server-side code artifacts; excluded with the no-code-execution rule. |

## 9. Paging: `limit`, `offset`, `filter`

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `limit n` (and `limit $p`) | `limit n` / `limit $p` | direct | Same bound rule (`n >= 1`, fail closed on 0). |
| `limit offset, size` / `limit size offset n` | separate `limit n offset m` clauses | translated | One paging form, SQL-familiar; the comma form is the most common RQL/SQL dialect trap. |
| `offset` keyword form | `offset m` | direct | Same. |
| `filter <expr>` (post-query filtering) | — not in tql/1.0 | accepted-difference | Single filter stage (`where`) keeps semantics simple and indexable under automatic indexing; V1's `filter`/`vector.search` exclusivity (QueryMetadata.cs) disappears with the clause. Revisit if parity evidence demands post-limit filtering. |
| `filter_limit n` | — not in tql/1.0 | accepted-difference | Follows `filter`. |

## 10. `load` and `include`

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `load path as alias, …` (and bare `load path alias`) | `load path as alias, …` | direct (bare alias translated) | Reference resolution carried over; `as` required like every alias. |
| `include path, …` | same | direct | Declarative eager fetch unchanged. |
| `include counters(…)` / `include timeseries(…)` | — not in tql/1.0 | accepted-difference | Family-gated: PB-3010 / PB-3020. |

## 11. Non-read clauses

| RQL | TQL | Disposition | Reason |
| --- | --- | --- | --- |
| `update { <JavaScript patch> }` | — none | accepted-difference | TQL is read-only and code-free; mutations are typed, idempotent, confirmable operations per the workspace's mutation discipline. |
| `declare function <JS>` (non-time-series) | — none | accepted-difference | Covered in Section 5 — the flagship no-code-execution difference. |
| Time-series `declare function` / `timeseries(…)` sub-queries | — not in tql/1.0 | accepted-difference | Family-gated (PB-3020); the grammar reserves nothing yet so the family design leads. |

## 12. Summary and handoff

Row counts by disposition (including gated rows at their base
disposition):

- **direct**: 42 rows (of which 8 are capability-gated until shipped)
- **translated**: 14 rows
- **accepted-difference**: 29 rows
- **TQL additions beyond RQL** (no parity row needed): quoted
  identifiers for dynamic fields, explicit `[]` any-element
  navigation, `missing()`, typed object/array constructors, `if()`,
  `coalesce()`, the separate `offset` clause, grammar versioning with
  fail-closed unknown-version rejection, strict parameter pairing,
  and capability gating itself.

Handoff to TQL-04: every `direct` and `translated` row needs one or
more conformance vectors (RQL text, TQL text, shared data, expected
identical results); every `accepted-difference` row needs a
WS-PARITY disposition proposal carrying the reason column verbatim;
gated rows additionally need their capability evidence before their
vectors can run. This file proposes, the parent disposes.
