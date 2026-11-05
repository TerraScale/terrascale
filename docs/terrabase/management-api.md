# Management API

TerraBase manages databases, placement, SQL execution, backups, recovery and usage. ts-auth owns identity, organization/project membership and customer API credentials. Dashboard owns saved queries, billing and UI state in its own SQL database. Services call the owning service's API.

Public routes use `/v1`. `/openapi/v1.json` and `/scalar/` are the current schemas for served routes, request bodies, permissions and errors. The public project database prefix is `/v1/projects/{project}/databases/{database}`. See [HTTP API](api.md).

## Authorization

Use ts-auth project access tokens and the operation's required scope. Database reads require `databases:read`, lifecycle mutations require `databases:write`, SQL reads require `sql:read`, and SQL mutations also require `sql:write`. Private service calls require the peer credential and the listener's service authorization; customer tokens never gain platform authority.

Credentials are checked before tenant data is returned. Missing membership, an out-of-scope database and a foreign project must not reveal resource existence. Invalid authority fails closed. See [cross-project errors](cross-project-errors.md) and [key revocation](key-revocation.md).

## Database lifecycle

Creation, rename, placement and deletion operate on immutable ULIDs. Where a guarded mutation accepts an expected ID, a same-name replacement is refused. [Agent databases](agent-databases.md) cover TTL, independent forks and scoped keys; [database rename](database-rename.md) describes name changes. Database allowances and storage admission apply before publication.

The [SQL catalog](sql-catalog.md) stores organizations, projects and databases. Internal system bookkeeping uses [plain rows](decisions/2026-11-04-plain-row-storage.md). It is never a separate customer data API.

## Recovery and deletion

[Continuous backup](backup.md) defines snapshots, authenticated backup objects, retention and point-in-time recovery. Recovery must establish a complete source window and retain tenant ownership, quota and expected-source checks. [Copy placement](recovery-copy-placement.md) covers publication and regional routing. A restore request is not proof of successful recovery until its operation completes.

[Deletion retention](deletion-retention.md) and [crypto-shredding](crypto-shredding.md) distinguish logical deletion, key destruction and physical cleanup. Independent forks retain their own lifecycle. No management response may claim erasure beyond the verified owning-service result.

## Usage and diagnostics

Project/database usage, analytics and logs come from TerraBase. Monthly writes are billed once where committed; SQL read fair use is separate and reads are free. [Operation allowances](operation-allowance.md), [database stats](database-stats.md) and [metrics export](metrics-export.md) define those measurements. Request observations omit SQL text, row values, credentials and query strings.

Every customer response includes `X-Request-ID` and the serving region. Authorization, cancellation and error responses preserve the same correlation boundary. Administrative pressure and peer transport remain private.
