# Collection schemas: definition, versioning, and write validation (SM-02)

Status: implemented for the engine seam; parity dispositions proposed, not
recorded. Ticket: TerraScale/TerraBaseV2#26 (SM-02). Binding decisions:
charter PD-2026-08-26-04, ADR 0007, and this record. Date: 2026-08-26.

This is the schema-format decision record ADR 0007 D-2 leaves to the
implementation ticket, the documentation of the typed schema model, and the
proposed parity mapping for WS-PARITY. It makes no recovery, replication,
multi-region, or performance claim.

## Decision: the schema format

A Document Schema is the accepted W3-F closed rule set — `Required`,
`FieldType`, `EnumValues`, `NumberRange`, `TextLength` over the six logical
field types — extended with a positive `SchemaVersion` identity, enforced by
a bounded deterministic evaluator, and carried bidirectionally onto a strict,
documented subset of JSON Schema draft 2020-12 for interoperability.

Evidence for each part:

- The rule set is not new: `FamilyKind::SchemaValidation` and its payload
  contract were accepted in W3-F (charter PD-2026-08-24-01 D4; the payloads
  and the `terrabase.families` record mapping already exist). SM-02 extends
  an accepted seam instead of inventing one.
- Bounded and deterministic by construction: at most 256 rules per schema
  and 256 accepted values per enum (enforced at construction), one bounded
  map lookup per rule, rules stored in canonical order (field name, then
  rule kind), violations reported in that order. Equal schemas project to
  equal wire documents regardless of declaration order.
- Wire compatibility with a *documented subset*: `to_json_schema` projects
  onto exactly the subset below and `from_json_schema` accepts exactly that
  subset, failing closed on every other keyword. The structured
  `DocumentValue` is the interchange value at every typed seam; its JSON
  byte serialization is the host/TBEP codec's concern (this crate has no
  JSON dependency and adds none).
- The crate carries zero dependencies by policy, so no third-party JSON
  Schema validator was available or adopted; the evaluator is engine-owned
  and auditable in one file (`src/families/schemas.rs`).

### Rejected alternatives

- **JSON Schema verbatim** (also rejected by ADR 0007 D-2): `$ref` graphs,
  `patternProperties` (a regex engine at the write path), and
  `oneOf`/`anyOf`/`if-then-else` combinators cannot guarantee bounded
  deterministic evaluation at the engine seam, and ecosystem drift would
  move the engine's acceptance set without an engine decision. The subset
  mapping keeps interop where it helps (import/export/tooling) and keeps
  the engine's semantics closed.
- **The full V1 `SchemaValidator` language now** (PB-3036 evidence:
  `src/TerraBase.Server/Documents/SchemaValidation/SchemaValidator.cs` with
  type, required, properties, additionalProperties, arrays, numbers,
  strings, constants, enums, references, conditionals, and pattern
  properties): references and conditionals are recursive and pattern
  properties need regex — both unbounded for this ticket's seam. V1
  language parity must be an explicit WS-PARITY disposition (below), never
  a silent permissive or restrictive drift.
- **An opaque typed blob without a wire mapping**: would keep evaluation
  closed but abandon the JSON Schema ecosystem interop the Platform surface
  and tooling expect.
- **Version-free schemas**: the glossary's Schema Version is the recorded
  identity under which documents are validated, and SM-03's migration
  contracts move collections *between* versions; without identities there
  is nothing to migrate between.
- **A schema cache in this ticket** (V1 caches validators per collection,
  PB-3037): caching is a performance concern that belongs behind
  benchmark-lane evidence (PD-2026-08-26-03), not inside the correctness
  seam. The hook reads the one validation record per put; no cache exists
  yet and none is claimed.

## The model

- `SchemaVersion`: a positive `u64` newtype. Version one is the first
  declaration; a strictly greater version evolves the schema; the same
  version with identical rules restates idempotently; any other repetition
  conflicts with the recorded version (`FamilyError::Conflict`). Clearing
  the schema (`ValidationWrite` with `None`) returns the collection to
  schema-free and ends the lineage; a later declaration starts fresh.
- `SchemaDefinition`: the version plus the rules, canonicalized at
  construction. Structural bounds (rule count, enum size, field names, the
  interchange domain below) are enforced at `try_new`; whole-definition
  semantics (inverted or vacuous bounds, empty or repeated enums, duplicate
  field-and-kind pairs, bound rules the field's declared type can never
  bind) are enforced at staging (`FamilyError::ValidationPolicyMalformed`).
- Interchange domain: the durable record and the JSON Schema projection
  carry `i64` integers, so a `SchemaVersion` or a `TextLength` bound above
  `i64::MAX` is rejected at construction
  (`RequestValidationError::InvalidFamilyPayload`). No bound or identity is
  clamped on the wire or in storage, so a stage-valid schema never loosens
  what was declared.
- Collection metadata: the schema is recorded in the reserved family
  collection at `family:validation/<collection>` — the W3-F record — now
  encoding `{version, rules}`. The version is part of the record: a record
  without a positive version does not decode.
- Adoption is never retroactive: stored documents are re-validated only on
  demand (`ValidationRead` with a document) or at the next write; the
  on-demand report carries the enforced `SchemaVersion` and the typed
  violations.
- The reserved collection `terrabase.families` cannot declare a schema
  (`FamilyError::InvalidRequest`): family records are engine state, and a
  schema on the schema store would gate its own bootstrap.

## Engine-seam enforcement

`Transaction::put` enforces the addressed collection's schema before
staging (`crate::families::schemas::enforce_put`, the SM-02 hook):

1. Documents of the reserved collection are exempt (engine state).
2. The validation record is read through the transaction itself, so a
   schema declared earlier in the same transaction already binds the
   transaction's own puts — there is no declare-then-write window.
3. No record: the put proceeds exactly as before — schema-free collections
   are unaffected (probe below).
4. Record present: the evaluator runs; violations reject the put before
   anything is staged, so there are no partial writes and the caller's
   transaction keeps its prior write set. The rule set rules object fields,
   so a non-object document value is itself a violation
   (`ViolationKind::DocumentNotObject`, empty field name) whenever the
   schema declares at least one rule — a schema'd collection never accepts
   a non-object put. Schema-free collections keep accepting every document
   value the document model carries, object or not.
5. A user document occupying the reserved validation-record address makes
   the collection's schema state unreadable; the put fails closed
   (`EngineError::StorageFailure`) rather than treating the collision as an
   absent schema — an occupied record must not become a validation bypass.

Error vocabulary (typed, tenant-scoped, content-safe — a violation names a
field and a rule kind, or the document itself with an empty field name for
`DocumentNotObject`, and never a field value or document body):

| Surface | Typed case |
| --- | --- |
| Engine `put`, violating document | `EngineError::InvalidDocument`, nothing staged |
| Engine `put`, non-object document in a schema'd collection | `EngineError::InvalidDocument`, nothing staged |
| Engine `put`, unreadable schema state | `EngineError::StorageFailure` (fail closed) |
| Family document-bound write, violating stored document | `FamilyError::SchemaViolation` (W3-F guard, unchanged) |
| Explanation: enforced version + violations | `ValidationRead` → `ValidationResult` (`enforced_version`, `violations`) |
| Schema wire import | `SchemaFormatError` (six closed cases, no echo of input) |

Tenant scoping: validation records are engine documents keyed by tenant and
identifier, so a schema belongs to exactly one tenant. A `ValidationWrite`
scoped to tenant A cannot be staged in a transaction bound to tenant B
(`FamilyError::TenantMismatch`), and tenant B's same-named collection never
sees tenant A's record. Both directions are probe-tested.

Mapping limit (recorded): the engine vocabulary has no reserved-state case,
so an occupied validation-record address maps to `StorageFailure`; a
dedicated engine error variant would be an integrator-owned
`src/engine/mod.rs` enum edit and is listed as a collision action.

## The documented JSON Schema subset

Projection and parser accept exactly this grammar (draft 2020-12 subset):

```text
schema    := { "$schema"?: "https://json-schema.org/draft/2020-12/schema",
               "type"?: "object",
               "properties"?: { field: subschema, ... },
               "required"?: [ field, ... ],
               "x-terrabase-schema-version"?: <integer> }
subschema := { "type"?: "boolean" | "integer" | "number" | "string" | "object" | "array",
               "enum"?: [ <string>, ... ],        ; non-empty, unique (enforced)
               "minimum"?: <integer>, "maximum"?: <integer>,    ; numeric types only
               "minLength"?: <integer>, "maxLength"?: <integer> } ; the string type only
```

- The logical `Text` type is JSON Schema's `string`; `Integer` and `Number`
  keep their JSON Schema names. Bounds are integers in the subset.
- A bound keyword binds only the type it addresses: `minimum`/`maximum`
  need `"integer"` or `"number"`, and `minLength`/`maxLength` need
  `"string"`. Any other pairing is rejected (`MalformedValue`) — a rule the
  evaluator could never fire is not imported as silent dead weight. A bound
  without a declared `type` is accepted and binds whichever numeric or text
  value the field carries. The typed model holds the same contract at
  staging, so a stage-valid schema always projects back onto itself.
- An `enum` names distinct values: a repeated value is rejected
  (`MalformedValue` on import, `ValidationPolicyMalformed` at staging of a
  typed declaration).
- `from_json_schema(version, value)` takes the version identity as a typed
  parameter; when the `x-terrabase-schema-version` extension is embedded it
  must agree or the parse fails (`VersionMismatch`).
- Any keyword outside the subset, any other dialect URI, any type outside
  the closed set, and any malformed keyword value fail closed with the
  typed `SchemaFormatError`. Nothing is ignored, so an imported schema
  cannot be silently weakened.
- Determinism: rules are canonicalized, projections use canonical map
  order, and `from_json_schema(to_json_schema(s)) == s` for every
  stage-valid schema (round-trip test).

## Tests (all through public seams)

In-crate (`src/families/tests.rs`):
`engine_put_enforces_declared_schemas_without_partial_writes`,
`same_transaction_schema_declaration_enforces_immediately`,
`schema_versions_evolve_forward_and_report_the_enforced_version`,
`schemas_are_tenant_scoped_and_cross_tenant_application_is_refused`
(probe), `schema_free_collections_are_byte_for_byte_unaffected` (probe:
identical scripts on two engines compare full durable trees byte-for-byte,
storage sequences step by step, and assert the reserved collection gained
no record), `reserved_collection_rejects_schema_declarations`,
`validation_record_collisions_fail_closed_on_put`,
`non_object_documents_fail_closed_under_declared_schemas` (every non-object
encoding is rejected at the engine seam, schema-free collections keep
accepting them, and a pre-adoption non-object document reports
`DocumentNotObject` on demand),
`validation_policy_rejects_duplicated_enum_values_and_inert_bounds`, plus
the reworked `number_range_binds_float_and_integer_values` (non-retroactive
adoption plus engine-seam rejection). Red-then-green: before the engine hook
landed, the four enforcement tests failed with `Ok(())` where the contract
requires `Err(InvalidDocument)`/`Err(StorageFailure)`; the non-object and
malformed-policy probes failed the same way before their fixes.

Integration (`tests/families/schemas_public.rs`):
`json_schema_subset_round_trips_exactly`,
`json_schema_parser_accepts_hand_written_subset_documents`,
`versions_and_text_bounds_stay_inside_the_interchange_domain`,
`json_schema_parser_rejects_duplicated_enum_values`,
`json_schema_parser_rejects_inert_bound_keywords`,
`json_schema_parser_rejects_keywords_outside_the_subset`,
`json_schema_parser_rejects_wrong_dialect_types_and_values`,
`schema_declarations_are_bounded`.

## Parity mapping (proposed only; WS-PARITY owns every disposition)

| Row | V1 fact (contract fragment) | V2 state after SM-02 | Proposed disposition |
| --- | --- | --- | --- |
| PB-3036 `schema-validation-config-errors` | V1 configures collection-scoped JSON schema validation, validates existing data on demand, rejects invalid writes with `SchemaValidationException` details; language includes type, required, properties, additionalProperties, arrays, numbers, strings, constants, enums, references, conditionals, pattern properties | Typed, versioned, bounded subset enforced at the engine seam on every put; on-demand validation reports enforced version + typed violations; no partial writes | `implemented` for the subset the V2 language covers, with an explicit **unresolved remainder**: additionalProperties, array items, const, references, conditionals, pattern properties are not in the V2 language. WS-PARITY should either file the remainder as a follow-up engine ticket or record an accepted-difference with this record as evidence; "no silent downgrade to permissive writes" is honored — every V1 rule kind is either mapped or explicitly absent from the documented subset. |
| PB-3037 `schema-validation-index-sharding` | V1 applies validators during writes and indexing, caches validators by collection, routes configuration through sharded handlers | Validation is engine-seam on write, not index-coupled; no validator cache; no sharding integration | `unresolved`: caching is deferred behind benchmark evidence (PD-2026-08-26-03); sharded/replicated application (a schema declared while members apply concurrent writes) belongs to the SH and replication dispositions. This record is the evidence input. |
| PB-3010 / PB-3040 (semantics bullet "indexes do not bypass schema validation") | Index/query paths must not bypass validation | Validation is at `Transaction::put`, upstream of every family, query, and index path; there is no bypass surface by construction | No disposition change proposed from this ticket; the query/index rows' own evidence continues to own it. |
| PB-5012 `storage-schema-upgrade` | V1 storage schema (format) upgrade | Distinct concept: storage format versioning, not Document Schemas | Untouched by SM-02; the glossary keeps Document Schema, Schema Version, Schema Migration, and Storage Format separate. |

Record disagreement noted for the parent: the SM wave spec states "V1 has
no enforced document-schema validation", while the authoritative parity
fragments (charter hierarchy item 3) record V1's `SchemaValidator` with
active tests. This record follows the parity fragments; the wave-spec
sentence should be corrected by the parent, not by this ticket.

## Limits and risks

- **Performance is unmeasured here.** The hook adds one validation-record
  lookup per put for schema-free collections (an absent-record fast path)
  and one bounded evaluation per put for schema'd collections. Per
  PD-2026-08-26-03 this is performance-relevant: benchmark-lane validation
  is **unrun** and must be scheduled by the parent before any performance
  statement; none is made here.
- No validator cache exists (contrast PB-3037); caching may follow with
  benchmark receipts.
- Replicated apply: a member applying a remote commit whose documents
  violate a schema declared concurrently on another member now fails closed
  at apply — the correct safe default, but the convergence contract for
  that interleaving is the replication wave's to disposition.
- Schema Migration (SM-03) is not implemented by this ticket; version
  identities and forward-only evolution are its inputs.
