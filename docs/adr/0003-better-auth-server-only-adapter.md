# Server-only Better Auth adapter

Status: Accepted boundary; production compatibility and service qualification
are pending.

Date: 2026-08-29

## Context

Better Auth needs uniqueness, conditional updates, one-time consumption,
pagination, transactions, and migrations. Those operations must use the public
Database API contract and preserve Better Auth error and retry semantics. A
raw HTTP callback, generic SDK request, direct TerraBase/TBEP connection, or
Dashboard private route would bypass the service boundary.

## Decision

Publish `@terrascale/sdk/better-auth` under a Node-only export condition. The
adapter accepts a named `BetterAuthDatabaseOperations` port and maps it to the
Better Auth database adapter contract. Mutations receive stable idempotency
keys. The adapter does not expose credentials to browser bundles and does not
provide a generic transport escape hatch.

The Go Database API in `ts-internals` owns the versioned operation
implementation. This repository does not invent its routes or claim that the
current structural port is a generated service binding.

## Consequences

The current unit tests prove mapping and error behavior with a local operation
port. They do not prove compatibility with a pinned Better Auth release or a
live Database API. Production requires the maintained Better Auth adapter
tests where available, exact core/plugin and license checks, migration,
revocation, uniqueness, compare-and-swap, one-time consume, Region contention,
retry, denial, and live integration tests.

The owning services must publish the exact Better Auth versions, permitted
plugins, and generated Database API binding before production qualification.
