# GAP-EVAL: engine query evaluator parity close-out (Q1..Q5)

Status: close-out disposition record for Forgejo ticket TerraBaseV2#62
(GAP-EVAL, folded gaps Q01..Q05 per charter decision
`PD-2026-08-26-14` D3), recorded 2026-08-26 against the `fleet/gapeval-2`
worktree of `develop` @ `2147dd4`.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`.worktrees/terrabase-v1-fa99b925-readonly` (read-only). Row-level gap
analysis: `docs/parity/ps-02-query-sweep.md` (rows PB-4004, PB-4005,
PB-4006, PB-4007, PB-4010, PB-4013, PB-4015 and the GAP-Q1..Q5 labels).
Every V1 file reference below is inside that pinned tree.

Test evidence: `src/query/tests.rs` (the GAP-EVAL section: execution
contract tests), `tests/query/evaluator_parity.rs` (public construction
and lowering contract tests), plus the updated TQL corpora
(`src/query/tql/parity_tests.rs`, `tests/query/tql_rql_parity_vectors.rs`,
`tests/query/tql_public.rs`) whose between/in/all-in/pattern/load/nulls
outcomes moved from not-planned to lowered with this packet.

Disposition vocabulary: **closed-here** (implemented and pinned),
**pinned-compatible** (no code change needed; the pin proves V1
compatibility), **deferred** (recorded residual with evidence and a named
gate), **accepted-difference** (a deliberate V2 shape, recorded).

## Q1 — NotEquals / null / missing / exists (PB-4004): pinned-compatible

V1 evidence:

- Lucene `NotEqual` is `MatchAllDocs MUST` + `Term MUST_NOT`
  (`src/TerraBase.Server/Documents/Queries/LuceneQueryHelper.cs:43-70`),
  so a document without the field is never excluded: it matches.
- Corax `NotEquals` is `AllEntries AndNot TermQuery`
  (`src/TerraBase.Server/Documents/Indexes/Persistence/Corax/QueryOptimizer/
  CoraxBooleanItem.cs:210-222`), the same all-entries-minus-term shape.
- `not X` in Lucene is `MUST_NOT X` + `SHOULD MatchAll`
  (`src/TerraBase.Server/Documents/Queries/LuceneQueryBuilder.cs:334-340`).
- A null literal is the explicit-null entry: CoraxBooleanItem's constructor
  keeps a null term as the null entry
  (`CoraxBooleanItem.cs:50-54` — the `Field != null` / `Field != ""`
  comment), so `field != null` excludes present nulls only and `= null`
  matches the null sentinel, never absence.

The W3-2 tracer semantics already match all four pins, so Q1 needed no
behavior change; the contract test
`v1_not_equals_matches_missing_and_pins_null_semantics`
(`src/query/tests.rs`) pins the V1 rules with these citations. The
analyzed-vs-exact mode distinction (analyzers fold case before the term
compare) has no V2 analyzer yet and is **deferred** to the analyzer
foundation (GAP-Q13); V2's matching is the exact-mode rule.

## Q2 — exact comparison, BETWEEN, dates (PB-4006)

**Closed-here — the 2^53 predicate-widening residual.** The predicate
comparator (`compare` in `src/query/model.rs`) now runs the exact
cross-encoding comparison (`compare_integer_with_number`) for mixed
Integer/Number pairs, the same helper the order-by path already used.
Red test first: `predicate_comparison_is_exact_across_integer_and_number_
domains` failed against the widening implementation
(`Integer(2^53 + 1) > Number(2^53.0)` answered false) and passes on the
exact one. The W3-2 design record's limits section
(`docs/query/w3-query-index.md`) now points here for the closure. V1
keeps Long and Double field types apart as exact terms
(`LuceneQueryBuilder.cs:202-226`), which makes the exact rule the
V1-faithful one.

**Closed-here — BETWEEN.** New `QueryCondition::Between { field, low,
high }` pins V1's `CoraxBooleanItem.BuildBetween`
(`CoraxBooleanItem.cs:105-160`) exactly:

- a null bound is unbounded on its side;
- both bounds unbounded matches every present value, explicit null
  included (the "matching lucene results, nulls included" comment);
- an unbounded upper side ORs the null entry in (`between x and null`
  matches present nulls);
- only-lower-unbounded is the plain upper comparison;
- two bounds are the inclusive range over orderable values, which absence
  and present null never satisfy.

The Lucene range-fusion path that builds a between from two comparisons
(`LuceneQueryBuilder.cs:232-252`) confirms inclusive bounds. TQL
`between a and b` lowers onto the condition (R12 outcome now "lowers").

**Accepted mapping — date ordering.** V1 orders dates through the exact
tick domain: a date literal becomes ticks and compares as a long
(`TranslateDateRangeQuery`, `LuceneQueryBuilder.cs:473-505`, and
`TryUseTime`; `CoraxBooleanItem.cs:98-107` converts the same way). V2's
typed value domain has no date literal, so the pinned mapping is: dates
as tick integers ride the exact integer order and same-format ISO-8601
strings ride the lexicographic string order. Pinned by
`date_valued_fields_order_exactly_in_the_integer_and_string_domains`.
A native date domain is **deferred** (it belongs with the typed value
domain decision, not the evaluator).

## Q3 — predicate expansion: IN, Contains, patterns, intersect
(PB-4005, PB-4007, PB-4010)

**Closed-here — membership.** `QueryCondition::In` (any) and `AllIn`
(every) pin V1: `WhereIn` and `ContainsAny` both lower to the In operator
(`src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.cs:746`,
`:1499-1510`), `ContainsAll` lowers to AllIn
(`AbstractDocumentQuery.cs:1516-1537`), and an empty ContainsAll lowers
to `TrueToken` — matches everything — while an empty any-in matches
nothing. V1's index flattens array values into terms, so one relation
serves scalar membership and array contains-any/contains-all: a scalar
field matches by equality, an array field when an element matches. TQL
`in (...)` / `all in (...)` lower onto the conditions (R13/R14 outcomes
now "lowers").

**Closed-here — prefix, suffix, substring.** `StartsWith`, `EndsWith`,
`Contains` conditions over the string domain (a string field directly,
any string element of an array field), case-sensitive — V1's exact-mode
rule. The analyzed-mode differences are **deferred** to GAP-Q13 with the
rest of the analyzer questions. TQL `starts_with`/`ends_with`/`contains`
calls lower (R37/R38 outcomes now "lowers").

**Deferred — regex.** V1's `regex` rides the term-level regex machines of
its two engines (`MethodType.Regex`, `LuceneQueryBuilder.cs:421-422`).
TQL `tql/1.0` has no regex production and no registry function, so the
engine holds no regex of its own: a regex spelling is `ParseError` today,
never a degraded pattern (pinned in
`tests/query/evaluator_parity.rs::tql_keeps_failing_closed_for_boolean_
arithmetic_and_regex`). Adding one needs a typed, bounded pattern
decision plus a vetted engine under the stack policy — re-registered as
a residual against PB-4007's regex half, gated on the TQL registry and
the analyzer/pattern foundation (GAP-Q13 coordination).

**Pinned-compatible — intersect.** V1 `intersect(...)` intersects the
results of its argument predicates — each argument is visited as a where
clause (`src/TerraBase.Server/Documents/Queries/QueryMetadata.cs:2433-
2440`), i.e. conjunction. The seam's predicate conjunction is that
semantics, pinned by `intersect_semantics_is_the_predicate_conjunction`.
A dedicated `intersect()` TQL spelling is **deferred** to the TQL wave
(the registry rejects it as an unknown function today, R42-R56 group).

**Rider — optimizer classification.** The demand derivation classifies
the new conditions (`src/query/optimizer/mod.rs::demand_of`): membership
and pattern conditions are term-set demands (equality capability),
`between` is a range demand (ordering capability), mirroring how V1
serves them. Pinned by
`dynamic_queries_with_expanded_conditions_select_through_the_optimizer`.

## Q4 — ordering parity (PB-4013)

**Closed-here — explicit NULLS placement.** `NullsPlacement`
(`Implicit`/`First`/`Last`) on `OrderingKey` (additive constructor
`try_new_with_nulls`; `try_new` keeps the implicit default) pins V1's
direction-independent placement: First is nulls-smallest ascending and
nulls-largest descending, Last the mirror
(`CoraxQueryBuilder.cs:1334-1339`; the client enum and its Corax-only
note at `src/TerraBase.Client/Documents/Session/Ordering.cs:15-43`). The
missing-and-null group moves as one block — an index sort cannot
separate an absent field from an explicit null — and inside the block
the seam's missing-before-null tracer order stays. All four explicit
spellings now lower from TQL (`asc/desc nulls first/last`); the TQL-04
corpus vector R69 and the public parity corpus record the new mapping.
V2 accepts the spelling on its one engine (V1 rejects it on Lucene);
recorded as an accepted-difference on the engine-availability surface,
not the semantics.

**Closed-here — random with seed (typed form).**
`QueryOrdering::random_with_seed(seed)` orders by a deterministic
FNV-1a hash of the seed and the document identity: one seed, one stable
permutation (pinned by `random_with_seed_orders_deterministically_
per_seed`). V1 derives the permutation from the seed argument
(`CoraxQueryBuilder.cs:1343-1347` hashes it; Lucene seeds `Random` with
the field hash, `RandomFieldComparator.cs:20-24`), and V1's two engines
already produce different sequences for the same seed, so matching any
one V1 sequence is not a parity obligation — determinism per seed is the
contract. **Deferred:** a TQL `random()` spelling (the registry rejects
it today, R71) and the unseeded form; both belong with the adapter-facing
result-shape decision (GAP-Q11) that owns observable stability.

**Deferred — score ordering.** V1's `order by score()` rides the scoring
model (`MatchCompareFieldType.Score`, `CoraxQueryBuilder.cs:1349-1357`),
which V2 does not have: no analyzer, no boosting, no scoring exists
(PB-4008 is GAP-Q13-gated). The registry's `score()` stays capability-
gated. No engine shape was invented for it here.

## Q5 — LOAD and computed projection (PB-4015)

**Closed-here — reference resolution (the source-document half).**
`QueryLoad { field, alias }` plus the additive `QuerySpec::with_loads`
bind each alias to the document whose id the source field names, exactly
V1's `LOAD` clause walk (`QueryMetadata.cs:1082-1136`: alias required,
duplicates rejected, id read from a field or parameter). Resolution is a
bounded id lookup in the same snapshot, not a join. A reference that
resolves to no document binds null (the TQL reference contract,
`docs/tql/spec.md` Section 4.2), a null binding navigates its projected
field to a present null, and an absent field of a resolved reference is
omitted — distinct from a present null, the seam's projection rule.
Projection names `alias` (the referenced document object) and
`alias.field` read the binding; TQL `load x as c` with
`select c.name`/`select c` lowers (R80's clause half). Pinned by
`loads_resolve_referenced_source_documents_into_projection`.

**Deferred — stored-field serving.** V1 serves a projection from the
index when the field is stored there; V2 has no stored fields — GAP-Q6
(#63) owns them and had not landed on this branch. The evaluator reads
the referenced source document, which is V1's non-stored path.

**Deferred — computed projections and where/order-by over loaded
aliases.** Computed projection expressions (object constructors,
arithmetic, function calls) and predicates over a loaded alias
(`where c.region = 'EMEA'`) parse in TQL and fail closed with the
construct named (`object constructor` / `path`), recorded in the corpora
(R34's computed half, R80's where half). They need a typed-expression
evaluation surface at the seam, which belongs with the TQL planner
extension and the adapter-facing result shape (GAP-Q11), not with this
evaluator's closed condition set. V1's array-alias loads (`alias[]`,
`QueryMetadata.cs:1113-1117`) have no `tql/1.0` spelling and are
**deferred** with the same surface.

**Closed-here (warden repair, loop 1) — reused ids resolve by the
engine's first-match rule.** Storage keys documents by tenant,
collection, and identifier, and nothing forbids reusing one identifier
across a tenant's collections — the same supported state the streaming
cursor rule records (`src/storage/root.rs`, `tenant_documents_from`).
The Q5 load map keyed tenant documents by bare id with silent
last-wins, so one snapshot answered the same bare id two different
ways: the load bound the last occurrence in canonical key order while
the engine's identifier-only point read (`Transaction::read` →
`read_document_at` → `find_by_document_id`) serves the first. Two fixes
were possible — first-match-wins, or a typed ambiguity error. The
repair picks **first-match-wins**: it is the engine's own existing id
resolution (agreement beats a new error), and it keeps the load a
lookup, not a policy decision. `by_id` in `src/query/execute.rs` is
built with `entry(id).or_insert` over the snapshot's canonical key
order, so a load binds exactly the document the point read serves; a
caller that needs a specific copy uses the collection-qualified read
(`read_in_collection`), as with the point-read surface. Red test first:
`loads_resolve_a_reused_id_the_way_the_point_read_does` committed one
id in two collections, observed the point read serve the alpha copy
while the load bound the beta copy, and passes on the agreed
first-match answer. The typed-ambiguity-error alternative is recorded
here as rejected: it would make reuse a query-time failure for a state
the engine already serves deterministically.

**Closed-here (warden repair, loop 1) — NaN is unordered in the mixed
predicate arms.** The Q2 exact-comparison rewrite routed the predicate's
mixed Integer/Number arms through `compare_integer_with_number`, whose
finite-only precondition the rank gate enforces only in the order-by
path: with a NaN literal both sign gates were false and
`NaN.trunc() as i128` saturated to 0, so the predicate decided against a
phantom 0 (`Integer(5)` answered `> Number(NaN)` true; a `Between` with
a NaN bound admitted half the range). The mixed arms now route NaN
around the helper (`mixed_numeric_ordering` in `src/query/model.rs`):
NaN compares unordered — `None` — making every comparison condition
against it false, the same rule the same-domain `Number` arm already
applies through `partial_cmp`; the infinities keep their sign-gate
answers (every integer is below `+inf` and above `-inf`). Red tests
first: `mixed_nan_literals_make_every_ordering_condition_false` pinned
all four inequalities against `Number(NaN)` literals and both `Between`
NaN-bound sides, and
`nan_documents_answer_no_integer_inequality_and_infinities_keep_their_signs`
pinned the symmetric `Number(NaN)`-document cases and the infinities'
unchanged answers.

Rider (same repair): the load-aware projection built its dotted alias
prefix with `format!` per matched row per projection name; the clause's
field, alias, and prefix are now precomputed once per query
(`LoadShape` in `src/query/execute.rs`) and per-row code only reads
them. Behavior-preserving; no red test exists for an allocation, and
the bare-alias and qualified-field projection arms are covered by
`loads_resolve_a_reused_id_the_way_the_point_read_does` and
`loads_resolve_referenced_source_documents_into_projection`.

## Shared machinery notes

- The predicate path, the order-by path, and the between evaluation now
  share one exact numeric comparison; the order-by comparator gains the
  placement and seeded-random modes without changing the implicit total
  document order (`compare_key`, `random_order_key` in
  `src/query/model.rs`). The sharded fan-out merge
  (`compare_documents_under_ordering`) inherits both through the same
  comparator, so fanned-out and single-shard results stay one order.
- New bounds stay in the seam's closed-value discipline: `MAX_IN_VALUES`
  (32) for membership lists, `MAX_LOADS` (8) for load aliases, non-empty
  pattern strings, and duplicate load aliases are rejected as V1 rejects
  them.
- `docs/tql/rql-conformance.md` rows R12, R13/R14, R34, R37/R38, and
  R80 moved from not-planned to lowered (or to their remaining
  not-planned half) with this packet; the language mapping dispositions
  in `docs/tql/rql-mapping.md` are unchanged.

## Limits

- No claim is made here about rows outside Q1..Q5 (PB-4008 search,
  PB-4016 includes, PB-4017/4018 distinct and grouping, PB-4025 result
  shape, and the rest of the PS-02 sweep); their dispositions stay with
  their owning tickets.
- Parity status records (`parity/overlays/**`) are outside this packet's
  write scope; this document is the evidence the parent adjudication
  uses for any status change.
- No performance claim is made; no benchmark lane was entered (nothing
  here asserts speed, and PD-2026-08-26-03's gates were not triggered).
