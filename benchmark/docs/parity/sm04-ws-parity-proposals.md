# SM-04 WS-PARITY proposals (ticket #28)

Proposals only, per the PS-wave rule that builders never flip overlays: the
dispositions below are for WS-PARITY (SE-12 close-out, #14) to record from
this packet's evidence. No overlay was mutated; `parity/v1-test-gaps.json`
is untouched.

Evidence base: decision record `docs/families/schema-surface.md`, the
engine suite `src/adapters/platform/schema_surface_tests.rs`, the TBEP
suite `crates/terrabase-tbep/tests/schema_surface_frames.rs`, the host
suite `host/TerraBase.Api.Tests/SchemaSurfaceTests.cs`, and the CLI suite
`cli/TerraBase.Cli.Tests/SchemaCommandsTests.cs`.

V1 citations are against the pinned read-only tree
`/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`.

## PB-3036 `schema-validation-config-errors` — propose `implemented-at-seam` (was seam-implemented)

The PS-03 sweep's evidence (closed rule set, malformed-policy rejection,
violation computation, write-path enforcement, on-demand reads) all stands;
what the row lacked was a public surface. The schema lifecycle now has one
end to end:

- Engine: `PublicOperation::Schema` with `Declare`/`Status`/`Validate`
  executing `ValidationWrite`/the recorded-policy read/`ValidationRead`
  (`src/adapters/platform/engine_port.rs`), each typed request validated at
  construction, cross-tenant claims refused
  (`schema_surface_tests::schema_surface_refuses_a_cross_tenant_claim`).
- Wire: TBEP `schema.declare`/`schema.status`/`schema.validate`
  (336–338 / 592–594), typed bodies only.
- Host: `PUT`/`GET /v1/schemas/{collection}` and
  `POST /v1/schemas/{collection}/validate`, gate-refused without backend
  context, tenant refusals surfaced and audited
  (`SchemaSurfaceTests.ASchemaDeclareForAnotherTenantIsRefusedTyped`).
- V1 shape check: V1 exposes the same three actions as database routes with
  a read/config authorization split — GET config `ValidUser` `Read`, POST
  admin config `DatabaseAdmin`, POST validate `ValidUser` `Read`
  (`src/TerraBase.Server/Documents/Handlers/SchemaValidationHandler.cs:9`,
  `:16`, `:23`).
  The V2 host's split is the local gate plus the backend-context headers;
  the V1 `DatabaseAdmin`-vs-`ValidUser` distinction maps to the V2
  principal/tenant claims and is worth a WS-PARITY note, not a gap.

Residual (unchanged from the sweep): a direct engine-internal write below
the family seam bypasses validation (GAP-F12 owns write-seam enforcement).

## PB-3037 `schema-validation-index-sharding` — propose `partial` (was seam-partial), two of three halves

- Public surface: now exists (PB-3036 evidence above), including the
  CLI `schema status`/`schema validate` and the studio page's typed
  `schema.status` read.
- Sharded/cluster visibility: V1 ships sharded schema-validation processors
  (`src/TerraBase.Server/Documents/Sharding/Handlers/Processors/SchemaValidation/`:
  `ShardedSchemaValidationHandlerProcessorForGet.cs`,
  `...ForPost.cs`, `...ForValidate.cs`) and a validator cache updated on
  configuration change
  (`src/TerraBase.Server/Documents/DocumentDatabase.cs:1813-1821`).
  V2 has neither a sharded schema path nor a validator cache; the V2
  engine re-derives the policy per write by design (bounded rule set,
  at most 256 rules, `MAX_SCHEMA_RULES` in `src/families/payloads.rs`),
  so the cache is an efficiency question, not a correctness one. Still
  SH-wave + GAP-F12 territory; no change proposed to the row's owner.

## New-capability proposal: `schema-migration-contracts` (registry parity string, not a V1 row)

The registry carries `migration.status`/`migration.dry_run`/
`migration.apply` (339–341 / 595–597) under the parity string
`new-capability: schema migration contracts (PD-2026-08-26-04, ADR-0007)`.
V1 has no schema-migration concept to mirror — a V1 schema change is a
config POST with no document rewriting (the POST processor extends
`AbstractHandlerProcessorForUpdateDatabaseConfiguration`,
`src/TerraBase.Server/Documents/Handlers/Processors/SchemaValidation/AbstractSchemaValidationHandlerProcessorForPost.cs:10`;
nothing rewrites stored documents). V2's declarative contracts, dry-run
plans, and apply receipts are new capability per the charter, and the
proposal is to record them as such rather than stretch a V1 row.

## New-capability proposal: `schema-revalidate-collection`

V1's `POST /databases/*/schema-validation/validate` runs an Operation with
progress over a collection's stored documents
(`src/TerraBase.Server/Documents/Handlers/Processors/SchemaValidation/SchemaValidationHandlerProcessorForValidate.cs:23-31`,
`OperationType.ValidateSchema`), so an operator can ask "which stored
documents violate the current schema?" — the 14-file V1 suite
`test/TerraBase.EndToEndTests/SchemaValidation/` exercises the validator
itself, not this operation. V2's nearest analog is the migration dry run,
which validates every transformed document against the target schema and
reports per-document problems (`plan_problem_kind::target_violations`), but
there is no standalone "revalidate the collection as-is" operation. If
WS-PARITY wants V1 behavioral coverage here, it is a small engine
operation (a `ValidationRead` sweep with bounded paging and a typed
problem list, no document bodies on the wire); this packet deliberately
did not build it because it is a scope decision, not a parity debt with a
charter obligation.

## Proposal: record the host-authorization vocabulary difference as `qualified`

V1 gates schema config behind `DatabaseAdmin` and reads behind `ValidUser`
(`SchemaValidationHandler.cs:9`, `:16`, `:23`); V2's host gates everything
behind the backend-operator context and the engine's tenant claims, with
lifecycle audit rows that carry no document bodies
(`SchemaSurfaceTests.TheSchemaLifecycleIsAuditedWithoutDocumentBodies`).
Same intent (a privileged mutation, an unprivileged read), different
vocabulary — `qualified`, not `implemented`, until WS-PARITY decides
whether the V2 principal model needs an explicit admin distinction.
