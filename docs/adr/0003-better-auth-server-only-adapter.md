# Server-only Better Auth adapter

Status: Accepted boundary; production compatibility and service qualification
are pending.

Date: 2026-08-29

## Context

Better Auth needs uniqueness, conditional updates, one-time consumption,
pagination, transactions, and migrations. Those operations must use the public
TerraScale API contract and preserve Better Auth error and retry semantics. A
raw HTTP callback, generic SDK request, direct Database engine connection,
private TBEP connection, or Dashboard private route would bypass the service
boundary.

## Decision

Publish `@terrascale/sdk/better-auth` under server runtime export conditions.
Node and Cloudflare Workers resolve the adapter; browser resolution is denied. The
adapter accepts a named `BetterAuthDatabaseOperations` port and maps it to the
Better Auth database adapter contract. Mutations receive stable idempotency
keys. The adapter does not expose credentials to browser bundles and does not
provide a generic transport escape hatch.

TerraScale.Database owns the versioned operation implementation and serves it
from the public API. It is the sole TBEP gateway; TBEP remains private control
traffic and is not a customer transport. This repository does not invent
routes or claim that the current structural port is a generated service
binding.

## Consequences

The current unit tests prove mapping and error behavior with a local operation
port. They do not prove compatibility with a pinned Better Auth release or a
live public API. Production requires the maintained Better Auth adapter
tests where available, exact core/plugin and license evidence, migration,
revocation, uniqueness, compare-and-swap, one-time consume, Region contention,
retry, denial, and live integration receipts.

The named `AuthPersistenceClient` now binds the producer's auth routes, and
`createTerraScaleAuthAdapter(client, database)` returns the official
`@better-auth/core` 1.7.2 adapter factory under the existing server-only export.
It receives the final Better Auth options, applies the maintained schema, ID,
date, and field transformations, and sends normalized explicit joins to the
producer. The internal options copy enables native joins for both ordinary and
transactional operations so related reads use the same producer snapshot.
Schema fields and compound indexes marked unique are provisioned through named index operations
before data operations or transaction begin; provisioning failures block use.
Fresh provisioning attempts use bounded keys, with each transport retry retaining
its original key, so a persisted failed index attempt cannot permanently poison
later schema repair.

`runAuthTransaction` uses the server-issued generation-qualified identity,
serializes at most 100 operations, and drains accepted work before commit or
rollback. The factory extends that ordered boundary around Better Auth's full
input/output transformations. Any failed operation poisons the callback even
if application code catches its error. The callback executes once. Lost or
malformed commit responses carry the original transaction and commit identities
for reconciliation; the helper neither reruns the callback nor rolls back an
uncertain commit. Rollback after cancellation has an independent bounded cleanup
signal and reports reconciliation identities if cleanup cannot be confirmed.

Producer publication, maintained upstream adapter suites, exact plugin/license
evidence, dashboard composition, schema migration beyond unique constraints,
and real multi-Region qualification remain required before production use.

One-time consumption accepts a record only when the authority returns
`matched: true` with an object record. An unmatched response must carry a null
record; contradictory or missing consumption results fail before Better Auth
can redeem the token. Such validation failures poison transactions even when
the application callback catches the error. Wire fault tests cover these
responses; they do not substitute for live authority contention evidence.

The temporary local SQLite bridge is still consumed by dashboard development
and cannot be retired before that replacement is qualified. Both its direct
operation port and loopback HTTP port reject transaction callbacks with
`transaction_unsupported` before invoking the callback. Previously they ran
callbacks as separate committed operations, allowing partial writes after a
later operation failed. The bridge now exposes its limitation without claiming
rollback. These development tests do not qualify production auth persistence.

The adapter accepts only the named `index` operation. The unconsumed
`ensureIndex` and `createIndex` aliases were removed after checking callers and
producer contracts; no published producer supplied those alternate spellings.

## Maintained suite against the local authority

`src/integration/auth-authority.test.ts` runs the published
`@better-auth/test-utils@1.7.2` normal, transaction, auth-flow, joins,
case-insensitive, and UUID suites. The package is MIT licensed and comes from
Better Auth tag `v1.7.2`, commit
`ba12fcdfa774ca27d417079dbac0b1b5894ccaf2`. Its exact dependency and registry
integrity are recorded in `package-lock.json`. Database-generated numeric IDs
are unsupported, so that suite is outside the selected contract.

Build this SDK with `npm run build`. Run the owning Database service's
published external-consumer fixture with absolute paths to Node and the
consumer script; the service repository owns the fixture command and its
daemon setup.

The producer creates a disposable two-voter encrypted SQLite authority with
QUIC and member certificates, serves the public auth router on loopback, and
passes its URL and credentials directly to the child process. The consumer
imports the built SDK and uses its network transport. Upstream cleanup deletes
test records; this runner accepts only a loopback fixture. Ordinary SDK checks
skip this test when the fixture environment is absent. The dedicated runner
rejects failures, skipped cases, empty runs, and unhandled errors.

Automatic unique-schema provisioning shares validated successes and in-flight
installs across adapters from the same client/database factory, keyed by
physical model and ordered fields. Failed installs remain retryable. Explicit
index operations and transaction-scoped indexes always reach the producer.

This fixture does not qualify daemon startup, OCI deployment, production key
delivery, WAN behavior, or multi-Region failure recovery. Its second voter uses
a test receiver loop. Passing local suites is required evidence, but does not
establish production readiness.

### Cleanup-corrected diagnostics

The published 1.7.2 utility retains every tracked row after a multi-row cleanup,
even when deletion succeeds. Later cases therefore repeat durable deletes for
historical rows. An unmodified run stopped after 38 passes on a 30-second test
and cleanup timeout; the same test passed in isolation.

Setting `TERRASCALE_AUTH_PATCH_UPSTREAM_CLEANUP=1` on the producer command enables
an explicitly labeled diagnostic correction. A test-only Vite transform in
`scripts/better-auth-cleanup-patch.ts` checks the exact upstream module path and
SHA-256 `2993733da66175a5e8a13180a15199ceb398e99439cf92619c24e08a012c9f33`
before replacing only cleanup tracking. Successful deletions leave the tracker;
failed deletions and unknown models remain for retry. Adapter assertions are
unchanged. The transform does not modify installed dependencies or SDK output.

The runner prints original and transformed source hashes and requires the
transform to have executed. Results use the distinct
`better-auth-1.7.2-public-authority-patched-cleanup` label and do not pass the
unmodified upstream-suite gate. `TERRASCALE_AUTH_TEST_NAME_PATTERN` is available
for focused diagnostics; filtered runs still fail qualification because the
runner rejects skipped cases.
