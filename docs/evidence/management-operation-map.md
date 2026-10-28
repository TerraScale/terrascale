# Management and administration contract reconciliation

Reviewed 2026-10-26 against Database/Authentication `a3cbf8b1c73d304189a489fa8033d41f3b508de3`, Dashboard `88d9ba39`, live public OpenAPI, Q11's full report and Q15's related Ku comments. Stale checkout HEADs and retired Go platform routes were excluded. The Q15 report was absent at the supplied path; its brief and relevant ticket evidence were available.

`createTerraScaleManagementClient` reuses the root project-scoped client; `db` below is `management.database(name)`. It preserves request/response types, cancellation, opaque cursors, errors and producer authorization. `createTerraScaleIdentityManagementClient` uses a separate platform sign-in JWT and Authentication origin. `createTerraScaleAdministrationClient` uses an administrator JWT. Construction grants no permissions.

| Dashboard operation family | Public SDK operations | Producer contract / boundary |
| --- | --- | --- |
| Organizations | Identity `createOrganization`, `listOrganizations`, `getOrganization`, `renameOrganization`, `deleteOrganization`, `leaveOrganization` | Authentication `/v1/terrascale/organizations`; current session and organization role |
| Projects | Identity `createProject`, `listProjects`, `getProject`, `renameProject`, `deleteProject`, `setProjectAllowedCidrs` | Authentication organization-scoped `/projects`; project/organization role; delete 202 means pending closure |
| Members | Identity `listMembers`, `changeMemberRole`, `removeMember`, `listProjectMembers`, `setProjectMemberRole`, `removeProjectMember` | Authentication organization/project `/members`; owner/admin checks and last-owner protection |
| Invitations | Identity `createInvitation`, `listInvitations`, `revokeInvitation` | Authentication organization `/invitations`; published role and invitation-lifetime rules |
| Ownership transfer | Identity `startOwnershipTransfer`, `getOwnershipTransfer`, `cancelOwnershipTransfer`, `confirmOwnershipTransfer` | Authentication organization `/ownership-transfer` and `/confirm`; owner and confirmation checks |
| Enterprise sign-in | Identity `getSsoConnection`, `putSsoConnection`, `deleteSsoConnection`, `verifySsoDomain` | Authentication organization `/sso` and `/verify`; administrative authorization and domain verification |
| Organization MFA | Identity `setOrganizationMfaPolicy` | Authentication organization `/mfa-policy`; producer enforces policy-change authorization |
| Organization audit | Identity `listAuditEvents` | Authentication organization `/audit-events`; authorized bounded pagination |
| Invitation acceptance | Identity `lookupInvitation`, `acceptInvitation`, `declineInvitation` | Authentication `/v1/terrascale/invitations`; opaque invitation token and current user checks |
| Account identity | Identity `getAccountMfa`, `accountLinkedProviders`, `accountUnlinkProvider`, `deleteAccount` | Authentication `/account`; current session, provider unlink confirmation and bounded asynchronous deletion |
| Deletion receipts | Separate `createTerraScaleAccountDeletionClient`: `accountDeletionStatus`, `accountDeletionHistory` | Receipt capability in the published named header; no bearer credential; logical completion does not certify physical erasure |
| End-user mint | Separate `createTerraScaleProjectIdentityClient`: `mintEndUserToken` | Authentication `/projects/{project}/end-user-tokens`; backend-only project API-key assertion, distinct from sign-in identity |
| Email admission | Excluded: service-only | `/v1/terrascale/email/admission` requires ts-alerts HMAC using a derived Cloudflare credential in `EmailAdmissionEndpoint.cs`; absent OpenAPI bearer security does not authorize a customer call |
| Organization keys | Identity `createOrganizationApiKey`, `listOrganizationApiKeys`, `getOrganizationApiKey`, `revokeOrganizationApiKey`, `rotateOrganizationApiKey`, `setOrganizationApiKeyAllowedCidrs` | Authentication organization `/api-keys`; owner/admin checks; one-time secret only on create/rotate |
| Project keys | Identity `createProjectApiKey`, `listProjectApiKeys`, `getProjectApiKey`, `revokeProjectApiKey`, `rotateProjectApiKey`, `setProjectApiKeyAllowedCidrs` | Authentication project `/api-keys`; effective member permission required |
| Database lifecycle | `databases.create/list/get/update/delete/branch` | Database `/v1/projects/{project}/databases`; databases:read/write; update changes placement, not name |
| Placement | `databases.create/update`, `metrics.replication`, `db.metrics.replication` | Existing placement DTOs and replication status; automatic discovery remains TS-507 |
| Backups | `db.backups.create/list/restore/getPolicy/setPolicy/window/plan/restorePointInTime` | Database `/backups`, `/backup-policy`, `/point-in-time`, `/point-in-time/plan`, `/point-in-time/restore`; bounded operations and typed partial outcomes |
| Backup destination | `backupDestination.get/set/delete` | Database project `/backup-destination`; scoped secret handling |
| Imports | `db.imports.create/get/uploadSource/update/start/cancel/resume` | Database `/import` workflow; upload offsets, preview confirmation, progress and unknown outcomes retained |
| Export | `db.export` | Database NDJSON export; authorized project/database scope |
| Usage/analytics | `metrics.usage/analytics/replication`, `db.metrics.usage/analytics/replication` | Database project/database usage routes; exact numeric counts retained |
| Schema/catalog | `db.schema.list/infer/declare/queryShapes`, collection `getSchema/putSchema/deleteSchema`, `getTtl/putTtl/deleteTtl` | Database published schema/TTL routes; prospective validation |
| Schema preview | Collection `previewSchema(schema, { cursor })` | Published `/schema/preview`; read + write scopes, raw schema bytes, bounded pages and redacted diagnostics; observational only |
| Indexes | `db.indexes.create/list/drop/recommendations/applyRecommendation` | Database public index routes; no invented readiness or migration guarantee |
| Saved queries | `savedQueries.create/list/get/update/delete` | Database project `/saved-queries`; existing opaque pagination |
| Data exploration | Root/`db.collection(name)` documents, query, SQL, transactions, shapes and webhooks | Existing root contracts; no new generic transport |
| Platform exploration | Admin `databases/collections/documents/document` | Database `/v1/platform/databases/...`; server requires platform_role=admin; read-only |
| Billing/administration actions | Omitted | TS-1104 owns missing public role-protected REST contracts; Dashboard cookie/CSRF checkout/admin routes are not bearer SDK routes |
| Durable schema plan/apply/status | Omitted | TS-1103; preview is not a durable plan and schema PUT does not apply a reserved migration |
| Database rename | Omitted | Existing TS-1050; no producer name PATCH invented |

Exact finite method/path/media/security/status descriptors are in `src/platform-management-routes.js` and `src/v1/{routes,management-routes}.js`; the Database inventory is `sdk-public-v1-route-manifest.json`. The new preview and identity operations have pinned served OpenAPI parity checks. Existing root operations retain their producer pins and tests rather than being reimplemented. Identity pages use `limit/after` and return `items/next`; mutations have no idempotency contract and are never automatically replayed. HTTP 401/403/conflicts and request IDs remain visible.

The Dashboard's published integration schema documents its session-cookie identity/workspace, credentials, changes and checkout flows. Those routes keep their supported paths; this SDK does not turn browser sessions or MCP tools into an arbitrary request API. Historical platform REST absence does not imply the Authentication organization's real routes are absent.

Qualification: HTTP fixture/contract tests and independently installed packed declarations establish SDK behavior only. Installed published-package live evidence must separately record the exact version, isolated account, successful operations, denial and cleanup. Administrator success needs a separately authorized administrator allocation; an ordinary account must remain denied. Better Auth is planned and has no public entrypoint from 0.0.14, as recorded in [the producer audit](better-auth-producer-audit.md).
