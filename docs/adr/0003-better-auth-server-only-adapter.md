# Server-only Better Auth adapter

Status: Accepted boundary; production compatibility and service qualification
are pending.

Date: 2026-08-29

## Context

Better Auth needs uniqueness, conditional updates, one-time consumption,
pagination, transactions, and migrations. Those operations must use the public
TerraScale API contract and preserve Better Auth error and retry semantics. A
raw HTTP callback, generic SDK request, direct `terrabase` engine connection,
private TBEP connection, or Dashboard private route would bypass the service
boundary.

## Decision

Publish `@terrascale/sdk/better-auth` under server runtime export conditions.
Node and Cloudflare Workers resolve the adapter; browser resolution is denied. The
adapter accepts a named `BetterAuthDatabaseOperations` port and maps it to the
Better Auth database adapter contract. Mutations receive stable idempotency
keys. The adapter does not expose credentials to browser bundles and does not
provide a generic transport escape hatch.

The Rust `terrabase` member owns the versioned operation implementation and
serves it from the public API. There is no separate database service or API
gateway, and TBEP remains private control traffic for the orchestrator and
CLI. This repository does not invent routes or claim that the current
structural port is a generated service binding.

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
Schema fields marked unique are provisioned through named index operations
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
