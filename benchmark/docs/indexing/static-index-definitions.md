# The typed static index definition language (GAP-Q06)

Ticket: TerraBase/TerraBaseV2#63 (GAP-Q06, folding GAP-Q8's
DISTINCT/static-`GROUP BY` half and GAP-Q16's legacy-JavaScript-index
disposition per charter PD-2026-08-26-14 D3). Implementation:
`src/index/definitions.rs` (public seam
`terrabase_v2::index::definitions`); focused tests: `tests/index/` (public
path, 14 tests) and `src/index/definitions_tests.rs` (in-crate, 9 tests).
WS-PARITY proposals authored by this ticket:
`docs/parity/gapq06-ws-parity-proposals.md` — no parity overlay is mutated
by this document or by the builder.

V1 source identity for every anchor below: commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`, read from the pinned clean
read-only worktree reached through `TERRABASE_V1_ROOT`. Citations use the
parity form `[v1:path::Symbol]`.

This is a design/decision record in the line of
`docs/indexing/dispositions.md`, not an ADR: ADR numbering is
parent-owned. Disposition vocabulary as in that document: **adopt**,
**adapt**, **accepted-difference**.

## The decision

**D1 — Static index definitions are typed values, not code.** V2 replaces
V1's code-authored maps and reduce functions with a closed, typed,
versioned value language: map stages project document field paths and the
document identity; the reduce stage groups by projected keys and aggregates
with a typed function set; field options are enums. This is the
PD-2026-08-26-08 D2 no-arbitrary-code rule applied to the index surface,
and it is also the Q16 disposition: V1's JavaScript map and map-reduce
index definitions are **replaced** by this typed language — an
accepted-difference with a migration path, not a compatibility surface
(the row-level record is in the WS-PARITY proposals file). V1's
`IndexDefinition` carries maps and reduce as source strings compiled at
deployment
([v1:src/TerraBase.Client/Documents/Indexes/IndexDefinition.cs::IndexDefinition]);
nothing of that execution model survives in V2.

**D2 — The language is versioned `idx/MAJOR.MINOR` and fails closed.**
Every definition carries the version it was authored against; any
well-formed version parses, and the engine constructs definitions only in
the version it speaks (`idx/1.0`), rejecting every other version before
any other validation runs. This follows the TQL grammar-version pattern so
stored definitions, migration tooling, and the TBEP operation surface can
share one version identity per artifact.

**D3 — Maps: sources, shapes, multi-map (adapt).** A map stage draws from
one collection or from all documents and projects an ordered output shape
of named bindings (`field path` or `document identity`). Multi-map is the
V1 multi-map model — one definition, several maps, one common result shape
([v1:src/TerraBase.Client/Documents/Indexes/AbstractMultiMapIndexCreationTask.cs::AbstractMultiMapIndexCreationTask];
[v1:test:test/TerraBase.SlowTests/Bugs/MultiMap/MultiMapReduce.cs]) —
adapted three ways: every stage must project the same ordered shape (V1
enforces this at compile time through the shared LINQ result type or fails
at runtime; V2 refuses at construction); duplicate sources are refused at
construction (V1's duplicate-map detection surfaces at query time); and
the all-documents source is exclusive — combining it with collection
stages would double-count every document of those collections in the
reduce, so the language refuses the combination rather than inheriting a
silent double count.

**D4 — Reduce: static aggregation with distinct grouping keys (adapt).**
The reduce stage carries grouping keys and aggregate bindings. The
aggregation vocabulary is V1's typed aggregation operation set for index
definitions — `count` and `sum` only
([v1:src/TerraBase.Client/Documents/Indexes/AggregationOperation.cs::AggregationOperation]);
min/max/average are not in `idx/1.0` and arrive as additive minor bumps
with their evaluator rows. The shape rule adapts V1's auto map-reduce
derivation — the map fields are exactly the group-by fields plus the
aggregation fields
([v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryMapping.cs::DynamicQueryMapping]):
the map shape must be covered exactly by grouping keys plus aggregation
inputs, every key and input must be projected, an input may not also be a
key, and no output may reuse a key's name. Nothing dangles and nothing
groups over unprojected data.

**D5 — DISTINCT and static `GROUP BY` semantics (adopt, index side).** A
map-reduce definition produces one output row per distinct grouping-key
value: the key set is the `DISTINCT` identity of the index output, and a
grouped query against the index adopts that grouping — the static half of
PB-4017/PB-4018. Array grouping carries V1's group-by array behavior
exactly
([v1:src/TerraBase.Client/Documents/Indexes/GroupByArrayBehavior.cs::GroupByArrayBehavior]):
`NotApplicable`, `ByContent` (one group per whole array value), and
`ByIndividualValues` (one group per element). The mode is part of the
definition's identity — changing it is a different index, not a silent
reroute. The dynamic (`select distinct`, in-query `group by`) halves of
those rows stay with the TQL/query seam per the TQL-04 proposals record.

**D6 — Field options: indexing, storage, analysis (adapt).** V1's
per-field options
([v1:src/TerraBase.Client/Documents/Indexes/IndexFieldOptions.cs::IndexFieldOptions];
[v1:src/TerraBase.Client/Documents/Indexes/FieldIndexing.cs::FieldIndexing];
[v1:src/TerraBase.Client/Documents/Indexes/FieldStorage.cs::FieldStorage])
are adopted for the typed subset the engine ships first: indexing mode
(`Default`, `Exact`, `No`, `Search`), storage mode (`NotStored`,
`Stored`), and analyzer name for analyzed fields. One adaptation is a
typed strengthening: an analyzer may be attached only to a `Search` field
— V1 validates that a named analyzer exists but tolerates analyzer text on
non-analyzed fields; V2 refuses it. Term vectors, spatial, vector, and
suggestion options ride their own rows (PB-4048/GAP-Q13, PB-4050/GAP-Q14,
PB-4051/CP-01) and compose with this contract later as additive minor
bumps. Options apply to the definition's output shape — map outputs for
map indexes, reduce outputs for map-reduce indexes — and an option naming
an unknown field is refused at construction. Analyzer *existence* and the
analyzer foundation are GAP-Q13's decision, not this contract's.

**D7 — Dynamic fields are typed rules (adapt).** V1's `CreateField`
runtime-named fields
([v1:test:test/TerraBase.EndToEndTests/Corax/DynamicFieldsTests.cs]) become
a typed rule per map stage: a name path, a value path, and the options
applied to every field the rule creates. The rule is validated at
construction; the created names remain runtime data, so nothing static —
grouping keys, field options, compound members — may name them, and reduce
over dynamic names is refused. This keeps V1's capability (indexing
attribute-shaped data without a fixed schema) without V1's code-execution
mechanism.

**D8 — Compound fields are a hint-only extension (adopt).** V1's
`CompoundFields`
([v1:src/TerraBase.Client/Documents/Indexes/IndexDefinition.cs::IndexDefinition])
are adopted as ordered member groups over the output shape. They advise
the storage engine and never change visible results; accordingly, a
compound-only definition change does not require a reindex — mirroring
V1's reindex mask, which excludes compound-field differences. Members must
exist in the output shape, be distinct within one compound, and no
compound may be declared twice.

**D9 — Output collections and reference patterns (adapt).** V1's
map-reduce output publication
([v1:src/TerraBase.Client/Documents/Indexes/IndexDefinition.cs::IndexDefinition];
[v1:src/TerraBase.Server/Documents/Indexes/MapReduce/Static/MapReduceIndex.cs::MapReduceIndex.ValidateReduceResultsCollectionNameAsync];
[v1:src/TerraBase.Server/Documents/Indexes/MapReduce/OutputToCollection/OutputReferencesPattern.cs::OutputReferencesPattern])
is adopted with its creation-time rules as construction-time typed errors:
an output collection requires a reduce stage; it must not be a collection
the index maps (infinite loop); an all-documents index may not declare one
at all; the references collection must differ from the output collection;
and one output collection belongs to one index
(`StaticIndexDefinition::output_collection_conflicts_with`), the
cross-index half of V1's rule, exposed as a helper so the catalog wiring
consumes one rule source. Reference patterns must carry at least one
placeholder, no repeated field, and only placeholders naming reduce output
fields. Two adaptations: V1's format suffixes (`{Field:format}`) are not
in `idx/1.0` and fail closed (they return with the date-format rows), and
placeholder-to-output-field binding — a runtime failure in V1 — is a
construction error in V2.

**D10 — Names, identity, and drift (adapt).** Static names follow V1's
rules: non-empty, no double slash, the V1 name alphabet, the dot-placement
rule, and refusal of the reserved `Auto/` namespace, compared
case-insensitively
([v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.IsValidIndexName];
[v1:src/TerraBase.Client/Util/ResourceNameValidator.cs::ResourceNameValidator]).
The reserved-prefix refusal agrees with the catalog's own rule
(`IndexError::AutoNamespaceReserved`, ADR 0010 AX-10) — pinned by an
in-crate test against `AUTO_NAMESPACE_PREFIX`. The byte bound is the seam's
identifier bound (255), not V1's path-length-derived character limit.
Map stages are stored in canonical source order, so map order is never
part of a definition's identity — V1 keeps maps as a set. `compare`
records drift by part (maps, reduce, field options, compound hints,
output publication) and `reindex_required` follows V1's reindex mask
adapted: maps, reduce, field options, and output publication require a
rebuild; compound hints do not. V1 folds output-collection changes into
its reduce difference; V2 reports them separately as output with the same
rebuild consequence. `identity_key` renders the full content identity
deterministically for drift and registration use.

## Rejected alternatives

- **R1 — Adopt V1's JavaScript maps and reduce verbatim (rejected).**
  Arbitrary code in index definitions is excluded by charter
  PD-2026-08-26-08 D2, and a scripting runtime in the engine contradicts
  the bounded-deterministic design of every other seam (the same rule that
  replaced RQL's JavaScript projections with typed expressions in TQL).
  This rejection *is* the Q16 disposition.
- **R2 — Untyped definition payloads (JSON/string maps, rejected).** A
  schema-free payload would carry unbounded, unvalidated content across a
  public seam and cannot fail closed by construction; every seam contract
  in this repository is typed for exactly this reason.
- **R3 — Reuse the optimizer's catalog `IndexDefinition` (rejected as the
  authoring language).** That type is the registration shape the catalog
  consumes (collection + ordered fields + kind); it has no reduce,
  multi-map, options, or output vocabulary. The typed language defined
  here feeds it: lowering a validated static definition into catalog
  registrations is IX-wave follow-on work (#38 explicit-vs-auto
  precedence), deliberately not wired by this ticket.
- **R4 — A TQL `CREATE INDEX` dialect only (rejected as the sole
  surface).** The language must exist as typed values behind typed
  operations (MCP-exposable per PD-2026-08-26-07) before any grammar; a
  TQL DDL surface can lower to this contract later without changing it.
  Grammar-first would couple index semantics to parser delivery order.
- **R5 — String-composed field flags (rejected).** V1 composes capability
  flag names as strings; the IX-01 dispositions already committed V2 to
  typed enums for field capabilities (AX-5), and this contract extends
  that decision to indexing/storage/analysis.
- **R6 — Family source types (time-series, counters) in `idx/1.0`
  (rejected for now).** V1 detects one source type per definition and
  refuses mixed ones
  ([v1:src/TerraBase.Client/Documents/Indexes/IndexDefinition.cs::IndexDefinition.DetectStaticIndexSourceType]);
  V2's first source kinds are collections and all-documents. Family
  sources are GAP-Q19's coordination point and arrive as an additive
  change.

## What this ticket does not claim

- No index **execution** changed: the catalog still builds single-field
  map segments; wiring static definitions into registration, building
  map-reduce segments, and serving grouped queries from index outputs are
  the IX wave's follow-on work. This ticket lands the contract, its
  validation, its identity/drift model, and the tests that pin them.
- No performance claim of any kind (PD-2026-08-26-03); compound fields in
  particular are a hint contract, not a speed statement.
- No parity overlay is mutated; rows stay `unresolved` until WS-PARITY
  dispositions them from the proposals file.
- The bounds (`MAX_MAP_STAGES`, `MAX_MAP_OUTPUT_FIELDS`,
  `MAX_REDUCE_KEYS`, `MAX_AGGREGATIONS`, `MAX_DYNAMIC_FIELD_RULES`,
  `MAX_COMPOUND_FIELDS`, `MAX_COMPOUND_MEMBERS`) are initial engine
  values, tunable with measured evidence.

## Test evidence

- Public seam (`tests/index/static_index_definitions.rs`, 14 tests):
  version parse/fail-closed; name rules incl. reserved namespace;
  map projection shape; multi-map common shape, unique sources,
  all-documents exclusivity; static `GROUP BY` shape and fail-closed
  reduce validation; array grouping modes pinned to V1; field options
  incl. analyzer rule; dynamic-field rules incl. static-name refusal;
  compound hints incl. hint-only drift; output-collection loop, pattern,
  reference, and cross-index conflict rules; drift comparison and
  reindex scope; deterministic identity keys; canonical option order.
- In-crate (`src/index/definitions_tests.rs`, 9 tests): the reserved
  prefix agrees with the catalog rule (`AUTO_NAMESPACE_PREFIX`); every
  bound fails closed and each bound is reachable (including the reduce
  aggregation bound); the version constant is wired; the reduce shape
  coverage rule; a dynamic rule's options participate in `identity_key`
  with the static options grammar.
- Red-then-green: the public test file was written first and failed
  against the unimplemented tree (`error[E0432]: could not find
  'definitions' in 'index'`) before the module existed.
