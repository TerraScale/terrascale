---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Multi-tenant authorization and isolation

## Decision

TerraBase uses defense in depth for the shared database service. The Go
Database gateway authenticates public callers, checks the public API action,
and resolves the caller to immutable TerraScale identities. TerraBase then
enforces the admitted database scope, current data-access policy, resource
limits, and reactive authorization generation. Neither boundary may rely on
the other boundary to repair a missing check.

Every request binds these separate identities:

- the certificate-authenticated gateway SPIFFE principal;
- the end-user or service `PrincipalUlid`;
- the `TenantUlid`, `ProjectUlid`, and `DatabaseUlid`;
- the caller session ULID and client-request ULID when applicable; and
- the authorization policy ULID and its current generation.

Tenant, Project, database, principal, policy, and session identities use the
canonical 16-byte TBEP representation in ADR 0027. Display names, external
identity-provider subjects, email addresses, route slugs, and caller-supplied
strings never become storage, routing, quota, encryption, or authorization
keys.

## Signed principal context

The gateway sends one short-lived signed `PrincipalContext` when it binds a
logical `terrabase-go` client. The context contains the complete identity
tuple, issuer and audience, issued-at and expiry times, a unique grant ULID,
the closed set of admitted operation classes, and the policy generation. Its
maximum lifetime is five minutes. It is bound to the exact gateway SPIFFE
principal and TerraBase environment, so another gateway identity or
environment cannot replay it.

The signed envelope carries an opaque payload, a bounded key identifier, and
an ECDSA P-256 with SHA-256 signature. TerraBase verifies the signature through
AWS-LC FIPS before decoding or using the payload. `ts-orchestrator` distributes
the trusted public-key set, rotates keys with overlap, and removes a retired
key only after every grant it signed has expired. Development uses a separate
owner-only local signing key and production rejects that key identity.

The bound context is immutable for the logical client. A scope, principal,
policy, or session change requires a new binding. Every RPC uses the
server-held binding rather than accepting replacement identity values from
request metadata. The engine rejects expired grants, unknown keys, wrong
audiences, duplicate identities, non-canonical ULIDs, and metadata that tries
to override the binding.

## Policy and revocation

TerraBase stores versioned authorization policy records and their generations
in the database control journal. A policy record names allowed operation
classes, collection and field visibility, relationship traversal, query and
index access, maintenance permissions, and any bounded row predicate. The
engine compiles policy before storage or index access. A deny result is the
default for an unknown action, field, collection, policy version, or claim.

The gateway can narrow a policy but cannot widen the durable engine policy.
Administrative service identities use the same model with explicit operation
classes. There is no superuser header, wildcard tenant, cross-tenant database
handle, or production bypass token.

Policy, membership, role, session, and relationship changes advance the
authorization generation in the durable publication journal. Ordinary calls
with an old generation fail closed and rebind. Reactive sessions apply the
transition rules in ADR 0044, including removal of data that the principal can
no longer see before a new checkpoint generation advances.

## Isolation and accounting

The Member resolves the admitted `DatabaseUlid` through its validated catalog
before it opens an engine location. It verifies that the database belongs to
the bound Project and tenant. Request values never form filesystem paths.

Admission, queues, caches, query work, reactive buffers, compaction, backup,
I/O, and usage facts charge the bound tenant and database. Global, tenant,
Project, and database limits apply in that order. Fair scheduling reserves
progress for other databases when one scope exhausts its allocation. The
engine returns a typed scoped refusal and does not borrow another tenant's
reserved capacity.

Logs, traces, metrics, and errors never expose end-user attributes, grant
bytes, signatures, raw external subjects, queries, or document data. Auditable
events use canonical ULIDs and bounded action and result codes. Prometheus
labels remain tenant-free under ADR 0036.

## Required evidence

Release evidence covers signature and key rotation, expiry, replay, wrong
audience, wrong gateway binding, malformed grants, non-canonical identities,
metadata override attempts, policy downgrade, revocation during a reactive
session, cross-tenant and cross-database access, catalog path traversal,
noisy-tenant saturation, dedicated-placement movement, redaction, and usage
attribution. The live matrix runs through the member's public API, mTLS,
the complete Member host, and at least two tenants with multiple users.

The release gate also proves that every public operation has one closed engine
operation class and that no default, wildcard, development key, local bypass,
or unauthenticated internal route is present in a production artifact.
