# GAP-Q10: patch-by-query and delete-by-query (the query-to-write bridge)

Status: decision record for Forgejo ticket TerraBaseV2#65 (GAP-Q10), recorded
2026-08-27 against the `fleet/gapq10-2` worktree of `develop` @ `1526cfd`.

Parity rows: PB-4037 `patch-by-query` and PB-4038 `delete-by-query` (core),
both `unresolved` in `docs/parity/ps-02-query-sweep.md:87-88` with the row
bodies at `:655-681`. This packet implements the **engine half** of both
rows: the typed query-to-write bridge with bounded page-wise execution,
per-page accounting receipts, audit-entry emission, an explicit confirmation
token for the delete half, and stale-allow behavior through the query seam's
own staleness policy. The **public path** — a TBEP frame family, the C# host
surface, and the interactive confirm step — is deliberately not built here
(D7). Parity status records (`parity/overlays/**`) are outside this packet's
write scope.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly` (read-only) [integrator rider: path corrected — gate NOTE]. Every V1 file
reference below is inside that pinned tree, cited by exact file and line.

Test evidence: `src/query/bulk_tests.rs` (the in-crate contract proofs:
confirmation refusal, page-wise deletion with per-page receipts, the
selection limit, strict-stale refusal, allow-stale execution, typed patch
application with NotModified accounting, the typed patch refusal that stops
a run, the bounded detail window, mid-run cancellation accounting, and the
audit journal) and `tests/query/bulk_by_query_contract.rs` (public
construction vocabulary). `TenantScope` has no public constructor by design,
so the execution proofs run in-crate, following the convention the engine,
query, and session seams already record.

Disposition vocabulary: **closed-here**, **pinned-compatible**, **defer**,
**accepted-difference** — used exactly as the neighboring records use them.

## D1 — V1's bulk-by-query, mapped from the pinned tree

V1 exposes two query-driven bulk mutations with one shared execution body:

- **Client surface.** `PatchByQueryOperation(queryToUpdate, options)`
  (`src/TerraBase.Client/Documents/Operations/PatchByQueryOperation.cs:38-42`)
  and `DeleteByQueryOperation(queryToDelete, options)`
  (`src/TerraBase.Client/Documents/Operations/DeleteByQueryOperation.cs:99-103`).
  Both post to `/databases/*/queries` with the same option spellings in the
  query string: `allowStale`, `maxOpsPerSec`, `details`, `staleTimeout`,
  `operationId` (`PatchByQueryOperation.cs:76-99`;
  `DeleteByQueryOperation.cs:132-149`).
- **Options.** `QueryOperationOptions`
  (`src/TerraBase.Client/Documents/Queries/QueryOperationOptions.cs:8-56`):
  `AllowStale`, `IgnoreMaxStepsForScript`, `StaleTimeout`,
  `MaxOpsPerSecond` (validated `> 0` at `:37-41`), `RetrieveDetails`, and
  `IndexPatchOptions` (wait-for-indexes after the writes).
- **Scheduling and audit.** The handler processor schedules one background
  operation and, when audit is enabled, writes **one audit line per
  operation** — action `DELETE` for `OperationType.DeleteByQuery` and
  `UPDATE` for `UpdateByQuery` — carrying the query text
  (`src/TerraBase.Server/Documents/Handlers/Processors/Queries/
  AbstractDatabaseOperationQueriesHandlerProcessor.cs:53-68`, the switch and
  the `LogAuditForDatabase` call at `:57-65`).
- **Execution body.** `ExecuteOperation`
  (`src/TerraBase.Server/Documents/Queries/AbstractDatabaseQueryRunner.cs:138-227`):
  1. refuse a map-reduce index (`:148-149`);
  2. rewrite the query into an **operation query** that keeps `Start` and
     `PageSize`, folds `options.StaleTimeout` into the wait, and projects
     **id only** (`ConvertToOperationQuery`, `:216-227`, `DocumentFields =
     DocumentFields.Id` at `:225`);
  3. resolve the matched **id set** against the index
     (`index.IdQuery`, `:166`) and **throw when stale under
     `AllowStale == false`** (`:167-168`);
  4. drain the id queue in **batches of 4096**
     (`const int batchSize = 4096`, `:153`), one transaction-merger enqueue
     per batch (`:184-196`), each batch applying one per-id command:
     `DeleteDocumentCommand(key, null /* change vector */)` for delete
     (`ExecuteDelete`, `:100-110`, the command at `:105`) and
     `PatchDocumentCommand` with `expectedChangeVector: null`,
     `skipPatchIfChangeVectorMismatch: false`, no `patchIfMissing`, no
     `createIfMissing` for patch (`ExecutePatch`, `:113-135`, the command
     construction at `:119-131`);
  5. optionally wait for indexes (`IndexPatchOptions`, `:204-209`).
- **Per-document details.** When `RetrieveDetails` is set, each command
  outcome is recorded as a detail — `DeleteDetails { Id, Etag, Collection }`
  or `PatchDetails { Id, ChangeVector, Etag, Status, Collection }`
  (`src/TerraBase.Client/Documents/Operations/BulkOperationResult.cs:67-101`;
  the recording path `RecordDelete`/`RecordPatch`,
  `src/TerraBase.Server/Documents/AdditionalPatchInformation.cs:47-70`).
  The patch detail's `Status` is the `PatchStatus` set, which includes
  `DocumentDoesNotExist` (`src/TerraBase.Client/Documents/Operations/
  PatchResult.cs:9-31`, the set GAP-D07 already inventoried): a patch on a
  document that vanished between selection and write is a recorded status,
  not an error.
- **Sharded mode.** The sharded processor fans the same command out per
  shard — the command factory takes the shard number
  (`src/TerraBase.Server/Documents/Sharding/Handlers/Processors/Queries/
  ShardedQueriesHandlerProcessorForDelete.cs:21-24`): each shard runs its
  own delete-by-query over its own data, and accounting composes per shard.

## D2 — the V2 bridge

`src/query/bulk.rs` owns the bridge. Two entries on the query store —
`QueryStore::delete_by_query(scope, request, cancellation)` and
`QueryStore::patch_by_query(scope, request, cancellation)` — each compile the
caller's typed [`QuerySpec`] as the selection, resolve the matched document
identities against one immutable snapshot, and publish the writes as **one
engine transaction per bounded page**. The patch half's per-document body is
exactly GAP-D07's [`DocumentPatch`] applied through
[`apply_document_patch`](crate::engine::apply_document_patch) — there is no
second patch language and no script body (charter `PD-2026-08-26-08` D2;
GAP-D07 D6.C already removed the script from this operation's future
surface). The delete half stages through a new minimal engine hook,
`Transaction::delete_in_collection` (D5), because the bulk operation targets
one collection by construction and the identifier-only delete would resolve
a reused identifier to whichever collection storage reaches first.

Both halves share one accounting model, one receipt shape, and one audit
journal. Nothing in the bridge is proportional to the selected set except
the identity list itself — V1's own shape (`Queue<string> resultIds`,
`:170`) [integrator rider: cite nudged — gate NOTE] with V1's own projection discipline (ids only, `:225`).

### D2.1 Options mapping

| V1 option | V2 shape | Disposition |
| --- | --- | --- |
| `AllowStale` (`QueryOperationOptions.cs:15`) | the spec's [`StalenessPolicy`]: `Strict` is V1's `allowStale=false` (a not-ready required index refuses the whole operation before any write — V1's throw at `AbstractDatabaseQueryRunner.cs:167-168`); `AllowStale` proceeds and the receipt carries the per-namespace staleness report | **closed-here** |
| `StaleTimeout` (`QueryOperationOptions.cs:28`) | not mapped: the engine seam has no bounded wait for index readiness; that wait is GAP-Q18's bounded-wait work at the index seam | **defer** (named gate: GAP-Q18) |
| `MaxOpsPerSecond` (`QueryOperationOptions.cs:33-44`) | not mapped: a rate gate paces a server operation across time; the engine seam executes synchronously and the host owns pacing | **defer** (named gate: D7 public path) |
| `RetrieveDetails` (`QueryOperationOptions.cs:49`) | `BulkByQueryOptions::with_details(true)`: the receipt carries a bounded drop-oldest window of per-document outcomes with the overflow counted and disclosed | **closed-here** (bounded — see D4) |
| `operationId` (`PatchByQueryOperation.cs:95-99`) | `DeleteByQueryRequest::with_operation_id` / `PatchByQueryRequest::with_operation_id`: the caller's stable run identity, carried on the audit entries; each page derives its own commit identity under it (D3) | **closed-here** |
| `IgnoreMaxStepsForScript` (`QueryOperationOptions.cs:20`) | structurally absent: no script executes, so no step budget exists to ignore | **accepted-difference** |
| `IndexPatchOptions` wait-for-indexes (`:53-56`) | not mapped: post-write index waiting is the index seam's staleness contract (GAP-Q18 owns the readiness/wait vocabulary) | **defer** (named gate: GAP-Q18) |
| `PageSize` on the query (`ConvertToOperationQuery` `:223`) | the spec's [`QueryPaging`] limit: a bounded selection caps the total documents the operation may touch; `Start` maps to the paging offset exactly as V1 carries it (`:221`) [integrator rider: cite nudged] | **closed-here** |
| batch bound 4096 (`:153`) | `MAX_BULK_PAGE_DOCUMENTS = 4096`, selectable down to 1 through `BulkByQueryOptions::try_with_page_documents` | **closed-here** |
| id-only projection (`:225`) | the selection materializes identities only: rows are consumed for `(collection, id)` and the write pages stage by identity; patch-by-query reads each document once inside its page transaction through the GAP-D07 seam | **closed-here** |
| map-reduce refusal (`:148-149`) | structurally unreachable: the V2 selection is a typed document query over a collection, and no map-reduce form exists at this seam to refuse | **pinned-compatible** |

### D2.2 Confirmation semantics for the delete half

Delete-by-query is the overbroad-deletion risk the parity row names
(`docs/parity/ps-02-query-sweep.md:678-679`). V1 has **no** confirmation on
this operation — a `DELETE /queries` with a broad predicate deletes broadly;
the interactive "type the collection name" gate lives in V1's Studio UI,
outside the operation. Charter `PD-2026-08-26-07` requires the opposite at
the typed seam: every MCP-exposable destructive mutation carries explicit
confirmation, and a non-interactive caller expresses it as a token, never a
prompt.

The bridge therefore refuses every deletion whose request carries no
[`DeletionConfirmation`] — a typed token whose only constructor is
`DeletionConfirmation::confirm_deletions()`, so the caller spells the intent
at the call site exactly as `IngestConflictMode::Overwrite` (GAP-D10 D4)
makes the destructive ingest an explicit selection. The refusal is typed
(`BulkTermination::Refused(BulkRefusal::ConfirmationRequired)`), happens
before any data access, emits a `Refused` audit entry, and the receipt's
counts stay zero — the refusal shape the RED test pins. The interactive
prompt itself belongs to the host/Studio surface (D7); the engine seam is
non-interactive by charter (`PD-2026-08-26-11`) and takes only the token.
Patch-by-query carries no confirmation gate: it is a field-level mutation
that rides the GAP-D07 vocabulary, not a deletion; its refusal shapes are
the typed patch refusals (D2.4).

### D2.3 Bounded page-wise execution

Selection runs against one immutable snapshot — the match set cannot shift
under it — in pages of at most [`MAX_PAGE_LIMIT`] identities (the query
seam's own page bound), honoring the spec's offset and limit, so a selection
larger than one page resolves through repeated bounded executions of the
same plan against the same snapshot. Writes then execute page-wise: the
identity list is chunked at the request's page bound (default 4096, V1's
batch), and **each page is one engine transaction** — one derived
[`OperationId`], one atomic commit, all or nothing — exactly V1's
per-batch transaction-merger shape. Each page takes a fresh snapshot, so a
page observes the published view as of its own start, the position of V1's
per-batch enqueue. Liveness gates (the caller's live cancellation signal by
reference and the spec's deadline) run between pages: a stop there leaves
earlier pages committed and accounts the untouched identities as
`unattempted`, never as processed — the GAP-D10 D2.3 discipline.

### D2.4 Refusal shapes

- **Per-document, without abort** (V1's recorded-status class): a target
  that vanished between selection and its page — the patch half's
  `MissingTarget`, the V1 `PatchStatus.DocumentDoesNotExist` status
  (`PatchResult.cs:9-31`) — is counted `refused`, disclosed in the detail
  window, and the run continues. The delete half treats a vanished target
  the same way rather than staging a tombstone for a key the page's
  snapshot no longer contains (**accepted-difference**: V1's
  `DeleteDocumentCommand` would publish a tombstone for a missing key; V2
  refuses to stage a phantom tombstone and says so in the receipt).
- **Terminal, stopping the run** (V1's batch-failure class): a typed patch
  refusal other than a missing target — `PathNotFound`, `TypeMismatch`,
  `ArithmeticOverflow` — stops the run with
  `BulkTermination::PatchRefused { operation, error }`, naming the refused
  page's commit identity; the page's transaction is dropped unstaged, its
  documents and every later page's are accounted `unattempted`. A rejected
  or unknown page commit maps to `Rejected`/`Unknown` with the page's
  operation identity for `Engine::resolve_commit`, exactly GAP-D10 D2.3's
  stop class.
- **Pre-execution**: the missing confirmation token (D2.2), an invalid
  request shape (construction), and selection refusals (`Selection`
  carrying the typed [`QueryError`], including the strict-stale
  `IndexNotReady` refusal before any write).

### D2.5 Audit entries

V1 writes one audit line per operation at scheduling time
(`AbstractDatabaseOperationQueriesHandlerProcessor.cs:55-66`). The bridge
emits into a bounded in-process engine journal
([`Engine::bulk_operations_audit`]): a `Refused(reason)` entry for a
pre-execution typed refusal (the missing confirmation token), a `Dispatched`
entry when the run passes its gates (V1's scheduling position), and one
terminal `Completed`/`Failed` entry — the
dispatched/completed/refused stage vocabulary the Platform adapter's audit
skeleton established (`src/adapters/platform/audit.rs`). A liveness stop
before dispatch (an already-cancelled signal, an already-expired deadline)
records the terminal `Failed` entry and never dispatches. Entries carry
structural identity only — kind, tenant, run identity, stage, selected and
processed counts. **accepted-difference**: V1's line carries the query text;
the V2 engine journal is content-free by the seam's audit discipline (no
document body, field, or query text), and the counts a V1 caller had to
poll progress for ride the terminal entry.

## D3 — operation identity: one `OperationId` per page

The engine's identity discipline binds one identity to one atomic
transaction. A bulk run therefore mints one identity per page —
`{run}-p{ordinal}` — never per document, exactly GAP-D10 D3's batch rule.
The caller's optional operation identity (`V1` `operationId`) names the
**run**: page identities derive under it and the audit entries carry it;
when absent, a process-wide monotonic counter mints the run identity
(`tbv2-bulkq-{counter}`, like the ingest stream's). Per-page accounting
rides [`BulkPageReceipt`] (commit identity, ordinal, documents, storage
sequence, durability) — the resolution handles a caller needs after a
rejected or unknown page.

## D4 — the accounting model

One receipt per run, both halves, reconciling exactly:

`selected == processed + not_modified + refused + unattempted`

- `selected` — identities the selection resolved under the spec's offset
  and limit (V1's id queue).
- `processed` — identities whose page committed: deletions staged and
  published, patches applied.
- `not_modified` — patch-half identities whose patched value equaled the
  original (GAP-D07's `PatchOutcome::NotModified`): V1's `NotModified`
  status, counted as processed-but-unchanged, staged nothing.
- `refused` — per-document refusals that did not stop the run (the missing
  targets of D2.4).
- `unattempted` — everything never published: a stopped run's forming page
  and every later page, a rejected or unknown page's staged documents, the
  remainder after a cancellation or deadline stop.

`is_complete()` is true only for `BulkTermination::Completed` (the selection
exhausted and every page published). Detail is bounded: the per-document
window retains at most [`MAX_BULK_DETAILS`] outcome records, drop-oldest,
with `dropped_details` disclosing the overflow — **accepted-difference**
from V1's unbounded `Details` list (`BulkOperationResult.cs:23`), the same
bounded-disclosure idiom GAP-D10 set. The per-page receipt list is O(pages),
never O(documents). **Per-shard composition**: a sharded deployment runs
this bridge per shard through the SH-04 serving endpoints, V1's sharded
shape (`ShardedQueriesHandlerProcessorForDelete.cs:21-24`); each shard's
run produces its own reconciling receipt and the composition sums
per-shard receipts. This packet owns the single-engine bridge; the fanned
out orchestration is the sharding wave's (D6).

## D5 — the one engine hook

`Transaction::delete_in_collection(collection, id, expected)` stages a
collection-scoped deletion without the identifier-only collection lookup
`Transaction::delete` performs. Storage keys documents by tenant,
collection, and identifier, and a reused identifier across collections is
legal (`Transaction::read_in_collection` exists for exactly this); a
bulk-by-query selection names one collection, so its deletions must resolve
by the same exact key. The hook stages the `StagedWrite::Delete {
collection: Some(..) }` the storage seam already defines — no storage
change, no new write path.

## D6 — rejected alternatives and non-goals

- **Re-query per page against a fresh snapshot** (instead of one id-set
  selection): rejected — deleting page N changes the match set, so offset
  paging over fresh snapshots skips or repeats documents unless the
  selection restarts from offset zero every page, which re-scans the whole
  matched prefix per page. V1 selects once (`resultIds`, `:170`) [integrator rider: cite nudged]; the
  bridge follows, and the ids-only bound is V1's own projection rule.
- **A script body per document** (V1's actual patch-by-query): banned by
  charter `PD-2026-08-26-08` D2; GAP-D07 D6.C records the rejection and
  this packet reuses that vocabulary as decided.
- **Per-document transactions**: neither V1's shape nor atomic; see D3.
- **Non-goals**: no TBEP frame family, host, CLI, or MCP surface (D7); no
  post-write index waiting (`IndexPatchOptions`) — GAP-Q18's wait
  vocabulary; no rate pacing — the host's; no fanned-out sharded
  orchestration — the sharding wave's composition over per-shard runs; no
  parity-overlay status move (the parent adjudication owns PB-4037/4038
  status from this record's evidence).

## D7 — proposed public path (not built here)

The host/TBEP packet should wrap the two entries as typed registry
operations carrying the request plus the caller's scope: (1) a TBEP frame
family over the UDS transport; (2) the interactive confirm step for the
delete half (Studio/host prompt) minting the `DeletionConfirmation` token,
with non-interactive agent callers passing the token explicitly per
`PD-2026-08-26-07`; (3) progress disclosure mapping the receipt's per-page
accounting; (4) retry mapping through `BulkTermination`'s operation
identities and `Engine::resolve_commit`.

## Test evidence protocol

The contract tests were written first and observed RED (unresolved-module
compile failure, the exact shapes recorded in the packet report), then
GREEN against the implementation. Behavior-testable contract rows and their
tests:

| Contract row | Test |
| --- | --- |
| Refusal without confirmation, pre-data-access, audited | `delete_by_query_refuses_without_confirmation` |
| Bounded page-wise deletion, per-page receipts, reconciliation | `delete_by_query_deletes_matches_in_bounded_pages` |
| Selection limit (V1 `PageSize`) caps the touched set | `delete_by_query_respects_the_selection_limit` |
| Strict stale refuses before any write | `delete_by_query_strict_stale_refuses_before_any_write` |
| Allow-stale proceeds and reports staleness | `delete_by_query_allow_stale_proceeds_and_reports_stale` |
| Typed patch application + NotModified accounting | `patch_by_query_applies_the_typed_patch_to_matches` |
| Typed patch refusal stops the run and reconciles | `patch_by_query_typed_refusal_stops_the_run_and_reconciles` |
| Bounded detail window with dropped-count disclosure | `bulk_details_window_is_bounded_and_dropped_count_discloses` |
| Mid-run stop: committed pages stay claimed, the failed page never | `delete_by_query_rejected_page_commit_claims_nothing` (injected pointer-publication fault) and `patch_by_query_typed_refusal_stops_the_run_and_reconciles` |
| Pre-cancelled signal stops before selection, zero counts | `delete_by_query_pre_cancelled_signal_stops_before_selection` |
| Audit journal records dispatched/completed pairs | `bulk_runs_record_audit_entries_on_the_engine` |
| Options bounds and confirmation token vocabulary (public) | `tests/query/bulk_by_query_contract.rs` |

Mid-run cancellation of a live signal is not behavior-tested: a cloned
[`Cancellation`] is an independent snapshot, so no in-crate sink can flip the
caller's signal mid-run — the mid-run stop accounting is pinned by the two
rows above instead, which exercise the same accounting path (a stop after a
committed page with the remainder unattempted).

One implementation rule the GREEN run surfaced and the tests now pin: a
write page whose every document was not-modified or refused stages nothing,
and the commit seam refuses an empty write set — such a page ends unstaged
(no transaction is published, no page receipt exists for it) and the run
continues, its observations already accounted. The alternative — committing
an empty transaction — is not a shape the engine offers.

RED evidence (observed before the implementation existed, exact compiler
output):

- `env -u RUSTUP_TOOLCHAIN cargo test --lib --offline --locked query::bulk`
  — `error[E0432]: unresolved imports super::bulk::BulkByQueryOptions,
  ... BulkDetailOutcome, BulkOperationKind, BulkRefusal, BulkTermination,
  DeletionConfirmation, DeleteByQueryRequest, PatchByQueryRequest,
  MAX_BULK_DETAILS, MAX_BULK_PAGE_DOCUMENTS` (`src/query/bulk_tests.rs:25`)
  and 16 further `error[E0599]: no method named delete_by_query /
  patch_by_query found for struct QueryStore` / `bulk_operations_audit found
  for struct Engine` errors across the twelve in-crate tests — 17 errors,
  `could not compile terrabase-v2 (lib test)`.
- `env -u RUSTUP_TOOLCHAIN cargo test --test query --offline --locked` —
  `error[E0432]: unresolved imports terrabase_v2::query::BulkByQueryOptions,
  ... DeletionConfirmation, DeleteByQueryRequest, PatchByQueryRequest,
  MAX_BULK_PAGE_DOCUMENTS` (`tests/query/bulk_by_query_contract.rs`),
  `could not compile terrabase-v2 (test "query")`.

GREEN evidence: the same two commands pass — 12/12 in-crate
(`query::bulk_tests`, `test result: ok. 12 passed; 0 failed`) and 5/5 public
(`bulk_by_query_contract`, `test result: ok. 5 passed; 0 failed`).

## V1 differences recorded

- **accepted-difference — no script body.** The per-document patch is the
  GAP-D07 typed vocabulary, never JavaScript.
- **accepted-difference — confirmation required for delete.** V1 has none
  on the operation; V2 refuses without an explicit typed token
  (PD-2026-08-26-07).
- **accepted-difference — content-free audit.** V1's audit line carries
  the query text; V2's engine journal carries structural identity and
  counts.
- **accepted-difference — bounded details.** V1 returns every per-document
  detail; V2 retains a bounded window with the overflow disclosed.
- **accepted-difference — no phantom tombstones.** A vanished delete
  target is refused-and-counted, not tombstoned.
- **accepted-difference — completion-time audit counts.** V1's audit line
  is written at scheduling with no counts; V2's terminal entry carries the
  reconciled counts.
- **defer — `StaleTimeout`, `IndexPatchOptions` wait** (GAP-Q18's bounded
  wait), **`MaxOpsPerSecond`** (host pacing).

## Limits

- The missing-target continue class (`refused` on a vanished target) is
  implemented and accounted, but a single-threaded behavior test cannot
  remove a selected document between selection and its page commit; its
  trigger is concurrent removal, and the row's evidence is the accounting
  invariant plus the V1 status cite, not a live run.
- No performance claim: nothing here asserts speed, and
  `PD-2026-08-26-03`'s benchmark gates were not triggered. Bounded
  page-wise execution is a structural claim (per-page transactions, an
  ids-only selection, bounded detail), not a throughput claim.
- No durability or recovery claim beyond what the engine commit seam
  already proves: the bridge adds no storage behavior; per-page atomicity
  inherits `Transaction::commit`.

## Integrator receipts (post-merge, develop)

- Focused gate receipts (clippy, fmt, both suites) reproduced green by the
  inspector and warden independently of the builder.
- Full `scripts/check.sh` on merged develop: recorded in the merge wave's
  integrator receipt at push time (the packet worktree ran it green at
  `8e58a33`; the merged-tree run covers the AC-3 obligation).
- `MAX_BULK_AUDIT_ENTRIES` added to the root re-export block by the
  integrator rider (warden NOTE: it was the only bound constant omitted).
