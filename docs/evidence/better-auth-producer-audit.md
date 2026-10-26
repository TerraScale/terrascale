# Better Auth producer feasibility audit

Scope decision, 2026-10-26: TS-1026 and TS-1085 are parked in Backlog until the
replication goal addresses the auth invariant design with TS-1007. Version 0.0.14
removes the public `./better-auth` stub; the adapter is planned. The audit below
records the earlier 0.0.9 source inspection, including the then-exported stub.

2026-10-26. Outcome: blocked on public producer contracts; no adapter implementation is justified.
Read the full TS-1026, TS-1007 and TS-757 issues/comments and applicable AGENTS.md files.
Producer evidence: fetched `TerraScale/terrascale origin/main` at
`a3cbf8b1c73d304189a489fa8033d41f3b508de3`, using `git show` rather than its different checkout HEAD.
SDK baseline: `993ac831767f0a4c2a5516d54b4a0830a021039d`; shared uncommitted work is excluded.
The SDK source snapshot at `contracts/terrabase-current-source/pin.json` pins older `0d2582d1`.
This report is source/contract inspection, not generated-contract parity or runtime qualification.

## Existing operations and missing guarantees

All producer paths below are relative to `src/TerraBase/` at the full producer SHA above.

- **Unique insert:** `PUT …/schema/{collection}`, operation `declareCollectionSchema`, exists.
  `Features/Management/Schema/PutCollectionSchemaEndpoint.cs:19` explicitly promises convergent
  uniqueness, not globally serialized admission during partitions. `Engine/Turso/
  TursoStorageTransaction.Unique.cs:127-152` rejects local collisions; replicated joins use
  `local:false` (`TursoStorageTransaction.Join.cs:109-144`) and hide a deterministic loser.
  Two regional callers can succeed before reconciliation; this cannot enforce factory uniqueness.
- **CAS:** document PUT/PATCH/DELETE implement `If-Match`; the no-If-Match claim in
  `docs/terrabase/replication/01-summary.md:23` is stale. `Features/Documents/
  DocumentService.cs:931-945` checks the accepting local transaction's version. Its `:528-544`
  commits before optional remote acknowledgement; that acknowledgement does not arbitrate CAS.
- **Consume/guarded increment:** no named public native equivalent of Better Auth `consumeOne`
  or `incrementOne` was found. DELETE returns tombstone metadata, not the consumed row.
  Query then conditional delete/increment cannot prevent dual regional success.
- **Transactions:** `POST …/transactions`, producer operation `runTransaction`, supports an
  atomic local batch of 1–1024 operations, at most 16 count assertions and 2 MiB captured changes.
  `Features/Documents/TransactionEndpoint.cs:27,95-96` has no transaction-scoped reads and
  disallows repeated document targets. Assertions see pre-write state (`DocumentService.cs:670`).
  The published batch alone cannot implement the factory callback's read-your-writes and rollback.
- **Pagination/count:** `queryDocuments` supports filters, sort, projection and bound cursors,
  1–1000 rows/page, 8 MiB/page. Count becomes a lower bound beyond 100000 examined documents
  (`Features/Documents/QueryDocumentsEndpoint.cs:17-27`). Offset can traverse cursors; exact count,
  joins, case-insensitive predicates and transaction reads still need a proven contract mapping.
- **Indexes/migrations:** named create/list/drop index and schema operations exist.
  `Features/Management/Schema/UniqueSchemaService.cs:34-39` uses `reindex:false`; changed uniqueness
  requires an empty collection. No public repeatable migration contract was established.
- **Retry identities:** `Features/Documents/DocumentRoute.cs:240-244` rejects Idempotency-Key.
  `docs/terrabase/decisions/0013b-operation-idempotency.md:1-13` leaves revision 3 pending.
  TS-1007 already owns durable receipts across regions/restarts, digest mismatch and metering.

## Exact pinned factory and server-only boundary

`better-auth`, `@better-auth/core` and `@better-auth/test-utils` are pinned to 1.7.2.
The correct factory import is `createAdapterFactory` from `@better-auth/core/db/adapter`.
Installed core `dist/db/adapter/index.d.mts:426-478,555-582` requires native single-row consume
(exactly one racing caller receives the deleted row) and guarded increment/set returning the row.
`dist/db/adapter/factory.mjs:699,738` throws when either native method is missing; no safe fallback.
The maintained transaction suite creates, queries its own write, awaits, throws, and checks rollback.
Read legacy `src/local/better-auth.js`, `src/local-better-auth.test.js` and the factory conformance
test: reusable concepts are factory transforms, custom names, ULIDs, uniqueness and settled handles;
their SQLite serialization/storage is not reusable producer evidence. Core's license is MIT.
Existing `./better-auth` exports target node/workerd and deny browser resolution (`browser:null`).
The entrypoint still throws; no supported production factory or browser bundle proof is claimed.

## Concrete owner handoff and remaining SDK work

Created **TS-1085**, Todo: “Publish public auth database primitives for the pinned Better Auth adapter”.
Owner: **TerraScale.Database public Database security-operation contract owner**; scope exceeds 400 lines.
TS-1026 is `blocked_by` TS-1085 and TS-1007; TS-1085 is `blocked_by` TS-1007. Relations verified in Ku.
TS-1085 requires named unique insert/CAS, native consumeOne/incrementOne, bounded async callback
transactions with read-your-writes/rollback, pagination/exact counts, indexes/migrations and scopes.
The owner must publish paths, operation IDs, DTOs, errors, cancellation/partition/unknown outcomes,
receipt semantics and OpenAPI/manifest parity before SDK implementation. No route/DTO is proposed here.
TS-757 remains Done under accepted ts-auth convergence, 300-second JWTs and its GRU-only live boundary.
That acceptance does not establish the different pinned factory guarantees; it is not reopened.
After producer admission, the SDK owner implements the server-only factory, publishes core/plugin
compatibility/license evidence, and runs exact-version maintained suites plus consume/increment,
Region contention, migration, retry, revocation, denial and installed-package/browser exclusion cases.
HTTP fakes are authorized for SDK conformance only; no TerraBase binaries belong in SDK tests.
Adapter suites and live qualification are **skipped** pending producer contracts/authorized allocation.
No tests/builds/lints were run for this documentation-only audit; no production action, code request,
signup, commit, push or PR occurred. Replication-owned files and other contributors' edits are untouched.
