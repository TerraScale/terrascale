# Remaining dashboard contracts (TS-493)

The 2026-10-06 service ownership decision in the TerraScale workspace `AGENTS.md`
supersedes the original TS-493 checklist. Services call each other through SDKs;
their application records remain ordinary TerraBase documents. Dashboard password
and email-code sign-in stay on dashboard pages.

## Ownership and existing implementation

| Requested behavior | Owner and implementation |
| --- | --- |
| Invitation resend | ts-auth owns invitations. The legacy TerraBase commit operation `resend_invitation` already replaces the token hash and records `invitation.resent`; `CommitApiTests` covers invalidating the old hash. Do not add another TerraBase invitation route. |
| Current project | ts-dashboard owns the selected project in its session; ts-auth owns the project and membership. Selection must not become a TerraBase catalog entity. |
| M2M credentials and key rotation | ts-auth owns client credentials, exchanged through SDKs for short-lived JWTs. TerraBase must not issue another credential kind. |
| Organization/project audit, filters and export | ts-auth owns the identity audit. The legacy TerraBase organization log has only pagination and retention; it is scheduled for removal by TS-589. |
| Database change audit | TerraBase produces database changes. TS-622 tracks audit telemetry in ordinary TerraBase collections and prerequisite work in each owning repo. Event destination, authorization, retention and delivery guarantees are not specified by the current API contract. Resolve those before extending the legacy organization log or promising complete event coverage. |
| Usage time series | Implemented by TS-581: database/project usage accepts `granularity=hour\|day`, `from` and `to`, with zero-filled UTC buckets. `UsageApiTests` covers persistence, aggregation and time boundaries. See [usage](management-api.md#usage). |
| Connection strings | The SDK resolves the service endpoint through Atlas, supports a static override and pins the selected region. Combine that session endpoint with the ts-auth project ID and database name; no secret or hardcoded regional hostname belongs in a TerraBase metadata response. See [region identity](region-identity.md). |
| Dashboard document browser | Use the existing collection, document list/get/query and conditional write APIs through an SDK. Pagination bounds item count and JSON bytes; cursors bind to their query and tenant. The special legacy dashboard document space stores dashboard application state, not a user's database browser. See [Documents API](decisions/0004-document-api-v1.md). |
| PITR | TS-633 already implements a restore window and restore into a new database. TS-493 adds a read-only preview using the same snapshot selection as restore, disclosing only metadata and leaving source data and catalog unchanged. See [backup restore](backup.md#restore). |

## Route transition

TS-589 implements public project-scoped routes, database names and ts-auth JWT scopes,
with hidden legacy `/v1/management/organizations/{organization}/projects/{project}` aliases
for existing callers. [The HTTP API table](api.md) describes the contract. Adding dashboard
identity endpoints to the legacy hierarchy would recreate work that this transition removes.

The existing data-plane collection/document routes already use
`/v1/projects/{project}/databases/{database}/collections/{collection}`. Their current
credential validation uses ts-auth JWT scopes; SDK consumers must follow the
authentication contract of the deployed version.

## SDK consumer work

The dashboard's checked-out ts-go dependency still expects older API shapes. Adding a
TerraBase route alone does not enable its dashboard workflow:

- PITR uses `/api/v1/databases/{name}/backups/restore`, Unix milliseconds and an
  asynchronous provisioning operation in that SDK. The C# implementation uses
  `pointInTime` as an ISO timestamp and returns the created database; it has no
  provisioning-operation compatibility API.
- The C# document list returns `{items, next_cursor}` and point reads return
  `{id, version, state, document}`. The dashboard's pinned SDK expects a different
  document-page body and raw JSON reads; its data service discards continuation.

Align typed SDK methods with the deployed C# contract, preserve opaque cursors and
ETags, and update the dashboard dependency before claiming an end-to-end browser
or PITR journey. Dashboard connection snippets can then compose the SDK's discovered
endpoint and project/database identifiers with ts-auth credential placeholders.

The inspected .NET auth SDK exposes `AuditEvents.ListAsync` but no operation for
ingesting another service's events. Choosing ts-auth as a destination for database
events therefore also requires an explicit ingestion contract and SDK method; a
raw cross-service HTTP call would violate the SDK-only service boundary.
