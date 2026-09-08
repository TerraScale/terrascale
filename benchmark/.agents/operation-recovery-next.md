# Durable operation recovery audit: index, migration, import, export

Read-only audit of the current `terrabase` server source and the pinned v1
consumer artifacts. No core product files were changed.

## Owner/API map

| Owner | Admission and durable record | Progress/recovery caller | Cancellation/retry |
| --- | --- | --- | --- |
| Schema/index maintenance | `Engine::maintain_index_backfills` updates durable index descriptor cursors (`terrabase/src/engine/mod.rs:4306-4324`). Schema-driven index registration is advanced by `schema_index_runtime::advance` (`terrabase/crates/terrabase-server/src/schema_index_runtime.rs:25-100`). | `schema_migration_runtime::drive_database` runs backfill and `maintain_indexes` before reading schema metadata (`terrabase/crates/terrabase-server/src/schema_migration_runtime.rs:252-264`); its 1-second `spawn` loop drives up to 8 databases per tick (`:317-359`). `index wait` polls `GET .../collections/{collection}/indexes` (`ts-cli/internal/cli/index_wait.go:98-107`). | No public cancel route. The maintenance pass is bounded/idempotent and resumes from durable cursor. `index wait` timeout/cancel only stops the client poll; rerun the same GET polling command. |
| Schema migration | `DurableSchemaRecord` stores migration state, saved dry-run evidence, and cursor in schema metadata. `MigrationCoordinator::migration_apply` is explicitly resumable (`terrabase/src/families/migrations/mod.rs:95-139`). | `schema_migration_runtime::drive_database` restores saved plan, calls `begin/apply_page/finalize`, writes `Running`, `Failed`, or `Complete` back to schema metadata (`terrabase/crates/terrabase-server/src/schema_migration_runtime.rs:171-315`). `spawn_schema_migrations` is started by the public server (`terrabase/crates/terrabase-server/src/production_data_runtime/public_server.rs:161-170`). Rollback is an explicit API (`schema_migration_recovery.rs:1-166`). | Forward migration has no cancel endpoint; failed reversible work can be rolled back through `POST .../schema/migration/rollback`, then resumed through the schema API. A stale worker is fenced by durable ownership/generation checks (`schema_migration_recovery.rs:116-165`). |
| Import | `ImportJob` in `ImportStore` (`terrabase/crates/terrabase-server/src/import_store.rs:12-34`) stores input, cursor, committed records, pending engine operation, and state. Admission is `POST /api/v1/databases/{name}/imports`; upload queues the job; resume is `POST .../imports/{id}/resume` (`api_imports.rs:590-775`). | `recover()` converts crashed `running` jobs to `queued` or `unknown` depending on `pending_operation` (`import_store.rs:230-235`). `recover_imports` relaunches queued jobs, while `spawn_imports` repeats recovery every second (`api_imports.rs:831-854`); public serving invokes both recovery and worker startup (`production_data_runtime/public_server.rs:161-170`). | Cancellation is process stop only (`ImportRuntime::stop/shutdown`, `api_imports.rs:24-65`); uncertain batches stay `unknown` and require authenticated `/resume`, which resolves the original engine operation before another write (`api_imports.rs:143-180,751-775`). |
| Export | `ExportService`/`SqliteExportStore` durably records job status, cursor, cancel flag, and staged artifact. `POST /api/v1/databases/{name}/exports` admits with `idempotency_key` (`api_exports.rs:210-245`). | `ExportRuntime::recover_pending` scans Accepted/Running records and runs bounded workers (`export_runtime.rs:108-144`). Public startup launches `recover_exports` (`api_exports.rs:360-377`; standalone startup task `runtime.rs:1611-1617`). | `POST .../cancel` sets a durable cancel flag; worker stops at the next durable chunk boundary. `POST .../resume` clears cancellation and reacquires the job (`export_api.rs:877-1018`). Reusing the same idempotency key returns the existing admission. |

## Concrete reachability/version gap

The import producer is implemented and routed in the server, but the pinned
contract does not publish it. `api_imports.rs` declares routes at lines
590, 672, 688, 751, and 856, and `api.rs:729-732` mounts them. Yet
`ts-rust/contracts/v1/openapi.json` has no path containing `/imports` (checked
with the executable command below), and the generated consumer route tables in
`ts-cli/internal/cli/generated_contract.go` and
`ts-cli/vendor/terrascale.tech/sdk/contract.go` have no import operations.
Consequently the durable import worker is unreachable through the accepted
public producer/consumer contract even though restart recovery exists in
server source. This is a source/version mismatch, not a worker implementation
failure.

## Executable producer brief for the root

1. Make the import route set an explicit v1 contract decision: publish
   `create_import`, `list_imports`, `get_import`, `upload_import`, and
   `resume_import` with the exact `ImportJob` serialization, authorization
   requirements, 202/200 statuses, and `unknown` recovery semantics; or remove
   the unreachable public import surface from the server in the same series.
2. Regenerate/update the pinned OpenAPI and all generated consumers together,
   then add a producer conformance vector that proves: a crash with no pending
   engine operation returns to `queued` and is auto-relaunched; a crash with a
   pending operation returns `unknown` and cannot write until `/resume` resolves
   the original operation identity.
3. Keep import cancellation bounded to process stop and explicit resume. Do not
   treat client disconnect or a returned timeout as proof that a pending engine
   batch did not commit.

## Reproducible checks

```sh
rg -n 'imports|schema/migration|collections/.*/indexes' \
  terrabase/crates/terrabase-server/src/api_imports.rs \
  terrabase/crates/terrabase-server/src/api.rs

python3 - <<'PY'
import json
p = json.load(open('ts-rust/contracts/v1/openapi.json'))
assert not any('/imports' in path for path in p['paths'])
print('pinned ts-rust OpenAPI import paths: 0')
PY

rg -n 'imports|create_import|resume_import' \
  ts-cli/internal/cli/generated_contract.go \
  ts-cli/vendor/terrascale.tech/sdk/contract.go
```

Expected result: server route declarations are present; the OpenAPI assertion
prints `pinned ts-rust OpenAPI import paths: 0`; the consumer search prints no
import operation rows.

## Source state and limits

Inspected source was the current shared workspace at 2026-09-07. No build or
tests were run due to compile freeze. No files besides this brief were changed.
