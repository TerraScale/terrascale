# Project JWT catalog bindings

Status: accepted by COS, 2026-10-07, TS-589 (option 1, verified bindings).

## Context

ts-auth owns projects; TerraBase databases reference a ts-auth project ID.
Legacy Management commits allowed ordinary organization members to choose
`create_project.id`. The resulting global `project_organizations` index records
storage placement, without proof that ts-auth authorized that organization to
represent the chosen project. Treating this index as JWT authority lets a legacy
user register another owner's ts-auth project ULID before its first JWT request.
Later JWT database writes could then be read by that legacy user's membership or
document keys. Equality between project and organization ULIDs proves no
authority either, including after a legacy organization is deleted.

## Decision

COS decided on 2026-10-07: "A JWT reaches existing legacy data only after the
project binding is verified against ts-auth (ts-auth owns projects; TerraBase
databases reference a ts-auth project ID). Unverified legacy mappings are not
trusted for JWT access; legacy (non-JWT) callers keep working."

TerraBase keeps a versioned binding recording the exact trusted issuer, project
ID, storage organization, verifying subject and time, plus the ts-auth
organization for an explicitly migrated legacy project. Fresh enrollment relies
on the signed project claim and records no ts-auth organization; `org_id` is not
required by the project-token contract.
Every project-JWT catalog resolution requires a binding matching its issuer and
storage namespace. An unverified legacy mapping answers `404`, including for
database creation; no fallback adopts or overwrites the mapping. Fresh JWT
enrollment creates its binding and opaque storage namespace in one transaction,
without creating legacy user membership.

Existing projects are bound through the hidden, client-only endpoint
`POST /v1/management/organizations/{legacyOrganization}/projects/{project}/binding`
with `{"organization":"<ts-auth organization id>"}` and the user's ts-auth
**sign-in** bearer token. The same signed subject must currently own the legacy
storage organization and have the effective `admin` role on the ts-auth project.
TerraBase forwards that sign-in token only to the configured issuer's ts-auth
management endpoint:
`{origin}/v1/{tenant}/organizations/{organization}/projects/{project}`.
The response must name the exact requested project and ts-auth organization and
return `role: "admin"`; ts-auth enforces live membership, sign-in revocation and
authentication epoch. Redirects are refused. Caller-supplied URLs and response
issuer URLs never select the verification target. Failure establishes no binding.
Legacy ownership is checked again inside the catalog write transaction.

Project/API-key tokens cannot establish a migration binding. Their `sub` may be
an API-key ID, and their data scopes do not distinguish project members from
admins. A loopback management credential alone likewise proves no ts-auth
project authority. Ordinary data requests continue to validate JWTs offline;
the live ts-auth lookup is restricted to this explicit migration action.

Client-chosen `create_project.id` now requires the loopback management service
credential. Ordinary legacy members can still create projects with generated
IDs. This restriction does not verify any historical mapping.

## Contract and storage implications

This decision changes the previous automatic legacy-mapping reuse described in
`docs/terrabase/api.md`, and the public client-chosen project-ID contract in
`docs/terrabase/management-commits.md`. Existing non-project-JWT callers retain access
under their legacy memberships and keys. Binding preserves catalog IDs,
database files, document versions, snapshots and replication identities; no
production wipe, file move or re-foundation is authorized.

## Validation requirements

Integration tests must drive the real listeners and cover the chosen-project-ID
takeover attempt, denial before verification, both ownership checks, failure of
ts-auth verification, preservation of legacy access and documents, and JWT
access to the same existing data after verification. Fresh enrollment must remain
isolated from live and deleted legacy organizations with matching identifiers.
