# Schema surface: typed operations, host endpoints, CLI, studio page (SM-04)

Status: implemented (engine operation set, TBEP registry, C# API host
endpoints, CLI commands, studio page); the engine-backed TBEP host remains
the recorded integrator action, so the host and CLI rows run against the
scripted TBEP engine that scripts the same typed wire answers the registry
defines. Ticket: TerraScale/TerraBaseV2#28 (SM-04). Binding decisions:
charter PD-2026-08-26-04 (schemas validated at the engine seam on write),
ADR 0007 D-3/D-4 (declarative migrations, typed-only exposure), the SM-02
record (`docs/families/schemas.md`), and the SM-03 record
(`docs/families/migrations.md`). Date: 2026-08-28.

This is the SM-04 decision record: the closed typed operation set over the
full schema lifecycle, the proof that no generic byte/frame schema endpoint
exists, the host/CLI/studio surfaces, and the RED→GREEN evidence of each. It
makes no replication, multi-region, or performance claim.

## Decision 1: the schema lifecycle is six typed operations in the closed registry

`PublicOperation::Schema(SchemaOperation)` joins the engine's closed
operation registry (`src/operations.rs`) with exactly six variants — no
more, no fewer:

| Operation | Engine execution (`src/adapters/platform/engine_port.rs`) | Mutation |
| --- | --- | --- |
| `SchemaOperation::Declare` | one `ValidationWrite` staged and committed through the family write path (`FamilyWriteRequest::schema_validation_update`) | yes (idempotency-paired) |
| `SchemaOperation::Status` | the recorded policy read (`recorded_policy`) | no |
| `SchemaOperation::Validate` | an on-demand `ValidationRead` over one supplied document | no |
| `SchemaOperation::MigrationStatus` | `families::migrations::status` | no |
| `SchemaOperation::MigrationDryRun` | `families::migrations::dry_run` (never commits) | no |
| `SchemaOperation::MigrationApply` | `families::migrations::apply` under `MigrationOptions` | yes (idempotency-paired) |

The closed-registry discipline is the whole point: adding a variant breaks
compilation until every exhaustive match in the platform adapter is
extended — tenant isolation, bounds, mutating identity, audit kind, and
forwarding (`src/adapters/platform/mod.rs`, `src/adapters/protocol/mod.rs`).
`schema_surface_tests::the_schema_operation_set_is_closed_and_typed` pins
the six-variant shape, and the six new `AuditOperation` rows
(`src/adapters/platform/audit.rs`: `schema-declare`, `schema-status`,
`schema-validate`, `schema-migration-status`, `schema-migration-dry-run`,
`schema-migration-apply`) ride the same audit pipeline.

## Decision 2: no generic byte/frame schema endpoint, proven twice

The generic family byte path (`family.read`/`family.write` over arbitrary
family names and opaque request bytes) cannot address the schema family:

- the TBEP registry carries no `bytes` field on any schema-family frame —
  the schema document rides as the protocol's typed `value`
  (`schema_surface_frames::no_schema_frame_carries_a_byte_form`);
- the codec's closed `FAMILY_KINDS` list excludes `schema_validation`, so
  sealing a `FamilyReadRequest` that names it fails at encode time
  (`schema_surface_frames::the_generic_family_byte_path_cannot_name_the_schema_family`).

The schema surface therefore has exactly one shape: the six typed
operations. This is ADR 0007 D-4's typed-only exposure rule applied to the
schema lifecycle, and it is what keeps the audit and tenant rows total.

## Decision 3: the wire is a registry block, fail-closed until served

The TBEP registry (`crates/terrabase-tbep/schema/tbep-registry.json`) grows
a `schemas` family block: codes 336–341 (requests) answering at +0x100
(592–597), budget class `schema` (65 536), mutations paired with the
idempotency-key discipline, and typed bodies only — the schema document as
a `value`, everything else identities, bounds, counts, and typed verdicts
(`violation_kind`, `migration_state_kind`, `plan_problem_kind`,
`rollback_limit_kind`, the `field_transform_step` closed vocabulary, and
the transform-literal variants). Parity mappings: `schema.*` carries
PB-3036/PB-3037; `migration.*` is new capability
(PD-2026-08-26-04, ADR-0007).

Following the GAP-R03 D4 precedent, the shipped host trait defaults the six
operations to a typed refusal and the server answers an unserved schema
request with `operation_refused` — never a misleading `malformed_frame`
(`schema_surface_frames::schema_requests_are_refused_typed_until_a_host_serves_them`).
Serving the frames from a live engine process through the C# host is the
engine-backed host integrator action this record names; until it lands, the
host and CLI rows below run the real host and the real protocol client over
`ScriptedTbepEngine`, which scripts the same typed wire answers.

Two codegen repairs fell out of the block and are part of this packet:
`HashCode.Combine` folding beyond eight arguments
(`crates/terrabase-tbep/src/codegen.rs`), and nil-tolerant enum formatters
for enums that appear as optional fields anywhere in the registry (the
migration-state fields of `schema.status` are the first such enums). A
third consequence is physical: the migration plan and receipt bodies grew
`TbepFrame` past 200 bytes, which the server's outbound queue enum now
pays for in copies — the writer's `Outbound::Frame` boxes the frame
(`crates/terrabase-tbep/src/server.rs`), surfaced by check.sh's
`--all-features` clippy after the plain all-targets run stayed under the
lint's threshold.

## Decision 4: host endpoints are audited without document bodies

Six routes on the C# API host (`host/TerraBase.Api/TypedApi.cs`):
`PUT /v1/schemas/{collection}` (declare, idempotency-key required),
`GET /v1/schemas/{collection}` (status),
`POST /v1/schemas/{collection}/validate` (validate),
`GET /v1/migrations/{collection}` (migration status),
`POST /v1/migrations/{collection}/dry-run`, and
`POST /v1/migrations/{collection}/apply` (idempotency-key required).
The local gate refuses all six before any engine frame without backend
context (401 `unauthorized`); the engine's bound channel enforces tenant
isolation and the host surfaces the refusal (403 `tenant-refused`, audited);
the lifecycle is audited under its own `AuditKind.SchemaLifecycle` rows and
the audit canary test proves no document body — and no schema document
value — ever rides an audit record.

## Decision 5: the CLI owns the operator actions; the studio reports

`schema status`, `schema validate`, `migration dry-run`, and
`migration apply` join the CLI grammar (`cli/TerraBase.Cli/`). The dry-run
output is canonical and byte-stable — the same command against the same
state answers byte-for-byte identically, with field order fixed by the
canonicalizer (`HostSurface.CanonicalPlan`) — so an operator can diff two
dry runs. `schema validate` exits non-zero on an invalid document;
`migration apply` requires `--acknowledge-not-rollbackable` when the
contract's rollback limit is `not_reversible`.

The studio's schema page (`studio/app/schemas/`) is a read-only report:
every collection with its enforced version, rule count, validation status,
and migration state, one typed `schema.status` per collection bounded to
32, plus one typed `query.execute` for the catalog. Live updates are the
named DB-04 seam (`SCHEMA_LIVE_SEAM` in
`studio/src/components/schemas/vocabulary.ts`): this packet builds no feed,
and the page renders the seam's landing state honestly.

## End-to-end scenario evidence

The ticket's scripted scenario — declare schema, invalid write refused,
migrate, valid under the new version — is
`schema_surface_tests::schema_lifecycle_scenario_declare_refuse_migrate_valid`
(`src/adapters/platform/schema_surface_tests.rs`): it declares version 1,
asserts the invalid write is refused by the validation hook (nothing
staged), applies the version-2 contract under
`acknowledge_not_rollbackable`, and asserts the same write shape that was
refused now validates and the collection records version 2. It runs in the
dev profile with `cargo test --workspace` (receipt 3) and in the release
profile with `scripts/check.sh` (receipt 4). The host-side lifecycle
round-trip (all six endpoints, same order) is
`SchemaSurfaceTests.TheSchemaLifecycleRoundTripsThroughTheTypedEndpoints`,
and the CLI row that seats the new version end-to-end is
`SchemaCommandsTests.MigrationApplySeatsTheNewVersion`.

## RED → GREEN evidence

Test-first per the packet rule; each suite was written first and observed
red, then implemented.

| Suite | RED shape | GREEN |
| --- | --- | --- |
| engine `src/adapters/platform/schema_surface_tests.rs` (4 tests) | 31 compile errors — `SchemaOperation`/`PublicOperation::Schema` absent, six exhaustive matches non-exhaustive, `AuditOperation` rows missing | 4/4 in `cargo test --workspace` (dev and release) |
| TBEP `crates/terrabase-tbep/tests/schema_surface_frames.rs` (6 tests) | compile errors — the generated schema frame types and the registry block absent | 6/6 in `cargo test -p terrabase-tbep` and the workspace run |
| host `host/TerraBase.Api.Tests/SchemaSurfaceTests.cs` (5 tests) | 404/405 on all six routes; `SchemaDeclareAsync`/five channel methods absent (compile) | 5/5; host suite 87/87 |
| CLI `cli/TerraBase.Cli.Tests/SchemaCommandsTests.cs` (5 tests) | with the five CLI implementation files stashed, the suite runs and the schema rows fail at runtime: `SchemaStatusReportsTheRecordedState`, `SchemaValidateNamesTheViolatedField` (`unknown command "schema validate"`), `MigrationDryRunEmitsAStableMachinePlan`, `MigrationApplySeatsTheNewVersion` — 4 failed / 123 succeeded, suite exit 2 (`AnUnknownOptionOnASchemaCommandIsAUsageError` passes in RED because an unknown command is also a non-zero usage error); the first capture attempt was inconclusive because the build still succeeds — commands dispatch by name, not by API — so the RED was re-captured by running the suite | 5/5; CLI suite green |
| studio `tests/schemas-page.test.mts` (15 tests) | `ERR_MODULE_NOT_FOUND` for `src/components/schemas/vocabulary.ts` and the fixture module; and `tests/contract.test.mjs`'s operation-vocabulary drift guard failed on `schema.status` before `tests/contract.test.mjs` was extended | 15/15; studio unit 103/103, `vp check` clean, conformance 15/15, e2e 48/48 |

The `tests/contract.test.mjs` extension is required, not incidental: the
drift guard asserts the studio contract's operation set mirrors the engine
`PublicOperation` set, and SM-04 adds `Schema` to that set. The studio
contract carries only `schema.status` (the page's read); the lifecycle's
other five operations are backend actions served by the CLI and the API
host, not studio page reads.

## Deliberately not done

- **Serving the schema frames from the engine process through the C# host**
  (the engine-backed TBEP host): remains the recorded integrator action per
  the GAP-R03 D4 precedent. The host/CLI rows script the wire answers
  instead; the engine rows execute the real engine.
- **SSE for the studio schema page**: DB-04 owns the live subscription
  surface; this packet leaves the typed seam named and renders its landing
  state.
- **V1's revalidate-existing-collections operation** (V1
  `schema-validation/validate`, an Operation with progress over stored
  documents): proposed for the parity backlog
  (`docs/parity/sm04-ws-parity-proposals.md`), not built here — the V2
  analog is the migration dry run, and a standalone revalidate row is a
  scope decision for WS-PARITY.
- **Studio write actions** (declare/validate/apply from the browser): the
  page reports recorded state only; the CLI owns the operator actions.

## Process note: the shared stash namespace

The CLI RED capture stashes the five implementation files and runs the
suite; the repository's stash namespace is shared across every worktree of
the repository, and a sibling packet's stash landed on top of mine between
the push and the pop, so the pop applied the sibling's single-file WIP into
this worktree instead of mine. The sibling's stash entry was never dropped
(git keeps it when a pop conflicts), its file was removed from this
worktree restoring this branch's state, and my own stash was then applied
by ref. Recorded here because the workspace runs multiple concurrent
builders against one repository: stash by ref, never by position.
