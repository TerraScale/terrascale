# SDK-auth integration contract

Unproven: these are source-derived contracts and HTTP-fake fixtures, not producer integration or live-service qualification. I5 must run producer parity with the selected SDK SHA and package; COS owns independent review.

Producer: TerraScale/terrascale `e30ffdc287530551b161b0130220be53d3c0475e` (fresh origin/main at execution). `provenance.json` hashes the inspected files. All fixture values are synthetic and authorize no live resource. `security.json` records trust/scope/error/boundary details. `routes.json` records exact operation IDs, paths, methods, request/response DTOs, statuses, media types, security and pagination. `dtos.json` records exact producer wire fields/types/nullability; SDK names are distinct from the retained legacy instance clients.

## Interface handoff to I3/I5

Use `TerraScaleAuthClient` with `InstanceSlug = "terrascale"` and the trusted auth origin. No management API key is needed for this slice. `AddTerraScaleAuthClient` registers it with redirects and cookies disabled. Caller-owned HttpClient handlers must also disable both; the SDK cannot inspect arbitrary handlers. Requests stay on the validated configured origin even if HttpClient.BaseAddress changes. Do not set a default Authorization header for OAuth forms/discovery.

`auth.Platform` is `TerraScaleAuthPlatformClient`. Every method takes `string accessToken` FIRST (the user's **sign-in** token, not the exchanged project token). IDs are canonical ULIDs. All methods end in an optional `CancellationToken`. Page methods take `int? limit = null, string? after = null` and return `TerraScaleAuthPlatformPage<T>(Items, Next)`. Pass Next unchanged to the SAME list; null starts a list, and empty `after` is rejected before dispatch. No auto-pagination/background work, no retries. The producer enforces authorization/roles/tenant isolation; the SDK does not infer user authority.

- Organization methods: Create/List/Get/Rename/DeleteOrganizationAsync, LeaveOrganizationAsync; ListMembersAsync, ChangeMemberRoleAsync, RemoveMemberAsync.
- Account methods: DeleteAccountAsync (DELETE /v1/terrascale/account) returns `bool`: `true` for 204 (deleted), `false` for 202 (a TerraBase project closure is pending; ts-auth finishes it in the background and calling again resumes it). Do not treat `false` as deleted: keep any local records of the account until a later call returns `true`. 409 `last_owner`, `not_empty`, `closure_conflict` or `binding_conflict`: before anything was removed the account stays (project closures already started finish on their own); once removals began, the deletion is paused until the cause is resolved.
- Project methods: Create/List/Get/Rename/DeleteProjectAsync; ListProjectMembersAsync, SetProjectMemberRoleAsync, RemoveProjectMemberAsync. DeleteProjectAsync returns `bool` the same way (204 `true`, 202 `false`: the project still exists until a later call returns `true`); 409 `not_empty`, `closure_conflict` or `binding_conflict` changes nothing. Any other success status is `invalid_response`.
- Key methods: Create/List/Get/Revoke/RotateOrganizationApiKey(s)Async and the equivalent Project methods. Create takes `TerraScaleAuthPlatformCreateApiKey`; rotate takes optional `TerraScaleAuthPlatformRotateApiKey`. Create/rotate return `TerraScaleAuthPlatformApiKeySecret(ApiKey, Key)`. Never persist/log Key as a DTO; display the secret once. Revocation prevents new issuance; already-issued exchanged tokens live at most 60 seconds. `LastUsedAt` is the last successful token exchange (null when never used; at most five minutes stale).

Hosted sign-in uses OIDC authorization code with PKCE. The producer removed the headless routes and password/email-code grants on 2026-10-22; the SDK no longer exposes their client or DTOs.

Token methods:

```csharp
Task<TerraScaleAuthTokenResponse> auth.Oidc.ExchangeTokenAsync(
    string clientId, string clientSecret, string subjectToken, string organizationId,
    string? projectId = null, string? scope = null, CancellationToken cancellationToken = default);
Task<TerraScaleAuthTokenResponse> auth.Oidc.RequestClientCredentialsTokenAsync(
    string clientId, string clientSecret, string? scope = null, string? project = null,
    CancellationToken cancellationToken = default);
```

Exchange uses the same confidential client that owns SubjectToken. **No database parameter exists in this producer's exchange grant.** A project API key can carry a database restriction; an organization API key optionally selects one project at token issuance. Key ID is a 26-character ULID (the producer ApiKeySecretBody comment incorrectly says 24). Full key string is client secret. The new optional project argument precedes CancellationToken in RequestClientCredentialsTokenAsync: existing positional fourth-argument cancellation callers must name that argument or supply project first. This is an explicit source API compatibility change in the 0.0.X candidate. Scope may be omitted from token responses. `IssuedTokenType` is optional RFC 8693 metadata. Sign-in scopes are only openid/email/profile/offline_access; action scopes come from exchange, never from hosted sign-in.

Use existing Oidc.RefreshTokenAsync, RevokeTokenAsync and GetUserInfoAsync for restoration/logout. Revocation ends the whole sign-in in ts-auth; offline validation still accepts issued tokens until expiry. Introspection is not served: its method/result have been removed, and the omitted discovery endpoint is nullable. Scoped producer provenance is in oidc-discovery-provenance.json. Retained instance Organizations represent an older contract and must not be used for platform management.

## Dashboard collaboration operation map

These eleven existing producer operations were missing from the initial `d6fbd4f`. I3's invitation, organization-access, ownership-transfer and audit services consume them, so I1 implements them in this same SDK scope with serializer metadata, manifests and HTTP-fake fixtures. I3 replaces its temporary HTTP calls with `.Platform` methods; I5 supplies the qualified package/composition and uses the corrected exact-head fixtures. Current-head CI, independent re-review, combined-package consumer qualification and producer parity remain acceptance dependencies.

All methods take the original user sign-in `accessToken` first and optional `CancellationToken` last. Organization-scoped methods next take `organizationId`. DTO names below omit the `TerraScaleAuthPlatform` prefix; lists return `TerraScaleAuthPlatformPage<T>`.

| Producer operation | SDK method | Request / result |
|---|---|---|
| `create_invitation` | `CreateInvitationAsync` | `CreateInvitation(Email, Role, ProjectId?)` / `CreatedInvitation(Invitation, Token)` |
| `list_invitations` | `ListInvitationsAsync` | `limit?, after?, projectId?` / page of `Invitation` |
| `revoke_invitation` | `RevokeInvitationAsync` | `invitationId` / 204 |
| `lookup_invitation` | `LookupInvitationAsync` | `InvitationToken(Token)` (no organizationId) / `InvitationLookup` |
| `accept_invitation` | `AcceptInvitationAsync` | `InvitationToken(Token)` (no organizationId) / `AcceptedInvitation` |
| `decline_invitation` | `DeclineInvitationAsync` | `InvitationToken(Token)` (no organizationId) / 204 |
| `start_ownership_transfer` | `StartOwnershipTransferAsync` | `StartOwnershipTransfer(UserId)` / `OwnershipTransfer` |
| `get_ownership_transfer` | `GetOwnershipTransferAsync` | no body / `OwnershipTransfer`; missing transfer throws typed 404 |
| `confirm_ownership_transfer` | `ConfirmOwnershipTransferAsync` | no body / `Member` with owner role |
| `cancel_ownership_transfer` | `CancelOwnershipTransferAsync` | no body / 204 |
| `list_audit_events` | `ListAuditEventsAsync` | `limit?, after?` / newest-first page of `AuditEvent` |

Invitation tokens redact `ToString`; never log/persist creation tokens or token requests. Lookup is signed-in and token-bearing; accept/decline additionally require matching verified email. Invitation project filtering uses the producer query name `project_id`. Creation may return 429; mutations never retry. Ownership/audit authority and membership updates remain ts-auth-owned.

## Fixture behavior derivation

`fixtures/platform-headless.json` uses a fixed synthetic clock of `2026-10-13T00:00:00+00:00` for creation and rotation, independent of the test runner's clock. Values were derived from the producer expressions below after verifying the pinned `provenance.json` hashes; the invariant test guards them. No producer binaries ran.

- `PlatformEndpoints.cs`: `Map(organization, Roles.Owner)` and `Map(project, Roles.Admin)` determine creation roles. `PlatformDirectory.cs` requires admin for both rename operations; rename fixtures use admin.
- `ApiKeyEndpoints.cs`: `ProjectId()` is null for organization endpoints; non-null database is rejected for organization creation. Organization-key request database and response project/database fields are null.
- `ApiKeys.cs`: `PreviousSecretExpiresAt = previousTtl > TimeSpan.Zero ? now + previousTtl : null`; the endpoint exposes this only while it is in the future. Both 60-second rotation fixtures expose `2026-10-13T00:01:00+00:00`.
- `Invitations.cs` / `PlatformDirectory.cs`: creation/transfer expiry is fixed clock + seven days, invitation status is pending, confirmation grants owner. Invitation tokens use the producer `tsi_` prefix and 52 synthetic alphabet characters. Test ID placeholders are replaced by independent ULIDs.

Contract tests verify these invariants, recursively compare exported operation fixtures to `dtos.json`, and check `security.json` error lists against the admitted vocabulary. `fixtures/project-invitation.json` additionally covers `create_invitation`, `lookup_invitation` and `accept_invitation` with a viewer role, non-null `project_id` and named project. Opaque pagination cursors and secrets are controlled HTTP-fake values; fake success does not prove a connected producer journey.

## Bounds and safety

The safe error vocabulary preserves `mfa_required` (403: organization access requires an active second factor)
and `mfa_not_enrolled` (409: the owner must enrol before requiring MFA). These deterministic refusals are not
retried. Only the fixed code and safe metadata are retained; unknown codes and producer messages remain omitted.
Their producer revision and source hashes are pinned in `error-codes.json` under `mfa_producer`.

Platform bodies: 16 KiB. Token forms and success responses: 1 MiB. Error responses: 64 KiB. One 30-second deadline covers transport/body work and preserves caller cancellation. TerraScaleAuthException exposes HTTP status, safe allowlisted ErrorCode and RetryAfter; unknown codes, producer messages/bodies and serializer details are not retained. Requests do not follow redirects when the documented handler prerequisite is met. The SDK never disposes caller-owned HttpClient. No storage, deployments, credential provisioning or service binaries are part of this contract. API-key metadata has no prefix or last4 field; do not fabricate either for list responses. The complete secret has format `<ULID>_<52 lowercase Crockford characters>` and appears only at creation/rotation.

## I5 requests

Package all `contracts/auth` manifests/fixtures with the SDK candidate and register them for compatibility checking; this slice does not edit csproj or root files. Keep package version 0.0.X (provisional 0.0.3 belongs to I5). Update root README/release notes to point consumers here and distinguish the C# platform client from legacy instance management. Run matching monorepo producer tests against the exact selected SDK K; do not infer parity from SDK fake tests. No dashboard endpoint/fixture composition or pin changes are made here.

## Project tenant management

`auth.Projects` is `TerraScaleAuthProjectsClient` for the producer's project tenant management API (`docs/auth/project-tenant-management.md` section 2, base `/v1/{project}`). Every method takes the caller's terrascale sign-in `accessToken` and the project ULID first and an optional `CancellationToken` last; viewer reads, admin changes, 404 hides projects the caller cannot see and 403 `forbidden` reports a role too low. Lists return `TerraScaleAuthPlatformPage<T>`. `TerraScaleAuthProjectOAuthClient.ClientSecret` appears only when creating a confidential client and is redacted from `ToString`. `fixtures/project-tenant.json` holds the HTTP-fake fixtures, with each operation's method, path and status; producer parity remains unproven. `ExportUsersAsync` returns one audited, admin-only directory page; see `user-export.md`.

## Retired instance clients

The unsupported `auth.Billing` client and its exclusive plan, seat-quota, checkout and portal DTOs have been removed. Its five operations used the legacy instance management key and `/v1/{slug}/billing...` routes; they are not wrappers for current public platform or dashboard billing. No replacement endpoint or adapter is provided. `Platform`, `Projects`, `Oidc` and their existing constructors/DTOs remain intact in this retirement phase.

Instance `auth.ApiKeys` and its exclusive `sk_` DTOs are retired. Use only existing producer-backed `Platform` organization/project key operations for their documented authority. Their models, CIDRs, credentials and routes are preserved; no legacy key is silently reinterpreted.
