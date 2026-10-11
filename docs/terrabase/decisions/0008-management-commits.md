# 0008: Management commits run in the system database, with a dashboard document space

- Status: proposed (TS-521, 2026-10-05); needs human acceptance
- Scope: `src/TerraScale.TerraBase/Features/Management`, `Features/Documents` (shared operation parsing), `docs/terrabase/management-commits.md`

## Context

A human decided for TS-472 on 2026-10-05 that ts-dashboard keeps its atomic `core.Store` guarantees on
TerraBase instead of weakening them. One Store update changes Management-owned state (organizations,
projects, databases, members, invitations, ownership, audit) together with dashboard-owned documents
(users, billing, checkout sessions, plans, …), and must publish all of it or none of it, with
serializable isolation over the aggregate.

The Management catalog lives in `_system.db` ([0005](0005-multi-tenancy.md)). Every tenant database is
its own SQLite file. SQLite runs in WAL mode, where a transaction spanning attached files is atomic per file
only: a crash between the two file commits can publish one and not the other. A commit spanning the catalog
and a tenant database therefore cannot be atomic without a TerraBase-owned two-phase protocol.

## Decision

- **One transaction.** `POST /v1/management/commits` applies Management operations and document operations in
  one write transaction of `_system.db`. The Management methods run unchanged inside it.
- **Dashboard document space.** The dashboard-owned documents that take part in commits live in `_system.db`,
  in collections `dashboard/<name>`, reachable only with the management service credential through
  `/v1/management/commits` and `/v1/management/documents/…`. No API key, no ts-auth user token and no tenant route
  reaches them. They
  are not a tenant's data: the dashboard is the trusted owner of that space. Management-owned state is never
  duplicated there.
- **Versions.** Every organization and account has a version, advanced by every Management change through any
  route. A `fence` operation is a compare-and-set on it, so the dashboard's aggregate lock is a Management
  primitive and not dashboard root CAS.
- **Durable outcomes.** A commit records its response under a client commit id in its own transaction, so a lost
  reply is resolved by `GET /v1/management/commits/{id}` or by resending the same body.
- **Files.** Database creation and deletion inside commits reuse the cleanup intents of TS-480: files are created
  before the transaction under a committed intent and removed after it.
- **Catalog version 5**, so older builds refuse the new data instead of misreading it.

## Consequences

- The system database also holds dashboard state; its size and write load grow with the dashboard. Commits are
  serialized by its single writer, as Management writes already were.
- Dashboard documents share the Documents API operation grammar, ETags and query v1, but not tenant features:
  no shapes, no per-commit storage limits of client databases (the 1024-operation and 4 MiB request bounds
  still apply), and no unique indexes or TTL (neither exists in the Documents API yet).
- Replicating `_system.db` (Management and dashboard state) across regions is still undesigned.

## Open question

[0007](0007-ts-auth-user-tokens.md) (TS-519) moved the Management API to ts-auth user tokens: ts-dashboard calls
with the signed-in user's token, and the management service credential works only on the loopback admin
listener. A user token must never reach the dashboard document space, which holds every user's dashboard state,
so this build fails closed: only the service credential reaches it. A remote ts-dashboard therefore needs a way
to prove it is the dashboard service while acting as the user for Management operations. For example, accept
document operations only from tokens issued to the ts-dashboard confidential client (an `azp`/`client_id` claim
in the TS-491 token contract), or let the service credential cross the network over mutual TLS. A human
decides this before TS-472 uses document operations.

## Alternatives considered

- **Commit across `_system.db` and a tenant `dashboard` database with `ATTACH`:** not atomic under WAL on a crash.
- **Two-phase commit with intents across files:** a TerraBase-owned commit protocol, far larger, and readers would
  need to resolve in-doubt state on every read.
- **Keep CRUD routes and let the dashboard compensate:** rejected by the TS-472 decision (no partially committed
  Management commands).
