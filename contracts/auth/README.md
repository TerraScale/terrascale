# SDK-auth integration contract

Unproven: these are source-derived contracts and HTTP-fake fixtures, not producer integration or live-service qualification. I5 must run producer parity with the selected SDK SHA and package; COS owns independent review.

Producer: TerraScale/terrascale `e30ffdc287530551b161b0130220be53d3c0475e` (fresh origin/main at execution). `provenance.json` hashes the inspected files. All fixture values are synthetic and authorize no live resource. `security.json` records trust/scope/error/boundary details. `routes.json` records exact operation IDs, paths, methods, request/response DTOs, statuses, media types, security and pagination. `dtos.json` records exact producer wire fields/types/nullability; SDK names are distinct from the retained legacy instance clients.

## Interface handoff to I3/I5

Use `TerraScaleAuthClient` with `InstanceSlug = "terrascale"` and the trusted auth origin. No management API key is needed for this slice. `AddTerraScaleAuthClient` registers it with redirects and cookies disabled. Caller-owned HttpClient handlers must also disable both; the SDK cannot inspect arbitrary handlers. Requests stay on the validated configured origin even if HttpClient.BaseAddress changes. Do not set a default Authorization header for OAuth forms/discovery.

`auth.Platform` is `TerraScaleAuthPlatformClient`. Every method takes `string accessToken` FIRST (the user's **sign-in** token, not the exchanged project token). IDs are canonical ULIDs. All methods end in an optional `CancellationToken`. Page methods take `int? limit = null, string? after = null` and return `TerraScaleAuthPlatformPage<T>(Items, Next)`. Pass Next unchanged to the SAME list. No auto-pagination/background work, no retries. The producer enforces authorization/roles/tenant isolation; the SDK does not infer user authority.

- Organization methods: Create/List/Get/Rename/DeleteOrganizationAsync, LeaveOrganizationAsync; ListMembersAsync, ChangeMemberRoleAsync, RemoveMemberAsync.
- Project methods: Create/List/Get/Rename/DeleteProjectAsync; ListProjectMembersAsync, SetProjectMemberRoleAsync, RemoveProjectMemberAsync.
- Key methods: Create/List/Get/Revoke/RotateOrganizationApiKey(s)Async and the equivalent Project methods. Create takes `TerraScaleAuthPlatformCreateApiKey`; rotate takes optional `TerraScaleAuthPlatformRotateApiKey`. Create/rotate return `TerraScaleAuthPlatformApiKeySecret(ApiKey, Key)`. Never persist/log Key as a DTO; display the secret once. Revocation prevents new issuance; already-issued offline tokens retain their lifetime.

`auth.Headless` is `TerraScaleAuthHeadlessClient`. JSON methods take `(string clientId, string clientSecret, request, CancellationToken)` and use form-encoded Basic authentication: SignUpAsync with `TerraScaleAuthHeadlessSignUp` returns `TerraScaleAuthHeadlessPendingSignUp`; StartEmailCodeAsync with `TerraScaleAuthHeadlessEmailCode` and StartPasswordResetAsync with `TerraScaleAuthHeadlessEmail` require 202; CompletePasswordResetAsync with `TerraScaleAuthHeadlessPasswordReset` requires 204. PendingToken and request secrets redact ToString. IP strings in JSON DTOs are the producer wire values; callers must supply proxy-validated end-user IPs.

Token methods:

```csharp
Task<TerraScaleAuthTokenResponse> auth.Headless.SignInWithPasswordAsync(
    string clientId, string clientSecret, string email, string password,
    IPAddress ipAddress, string? scope = null, CancellationToken cancellationToken = default);
Task<TerraScaleAuthTokenResponse> auth.Headless.SignInWithEmailCodeAsync(
    string clientId, string clientSecret, string email, string code,
    IPAddress ipAddress, string? pendingToken = null, string? scope = null,
    CancellationToken cancellationToken = default);
Task<TerraScaleAuthTokenResponse> auth.Oidc.ExchangeTokenAsync(
    string clientId, string clientSecret, string subjectToken, string organizationId,
    string? projectId = null, string? scope = null, CancellationToken cancellationToken = default);
Task<TerraScaleAuthTokenResponse> auth.Oidc.RequestClientCredentialsTokenAsync(
    string clientId, string clientSecret, string? scope = null, string? project = null,
    CancellationToken cancellationToken = default);
```

Exchange uses the same confidential client that owns SubjectToken. **No database parameter exists in this producer's exchange grant.** A project API key can carry a database restriction; an organization API key optionally selects one project at token issuance. Key ID is a 26-character ULID (the producer ApiKeySecretBody comment incorrectly says 24). Full key string is client secret. The new optional project argument precedes CancellationToken in RequestClientCredentialsTokenAsync: existing positional fourth-argument cancellation callers must name that argument or supply project first. This is an explicit source API compatibility change in the 0.0.X candidate. Scope may be omitted from token responses. `IssuedTokenType` is optional RFC 8693 metadata. Sign-in scopes are only openid/email/profile/offline_access; action scopes come from exchange, never from password/code sign-in.

Use existing Oidc.RefreshTokenAsync, RevokeTokenAsync and GetUserInfoAsync for restoration/logout. Revocation ends the whole sign-in in ts-auth; offline validation still accepts issued tokens until expiry. Retained IntrospectTokenAsync is **not supported** by this C# producer. Retained instance Organizations/ApiKeys clients represent an older contract and must not be used for platform management.

## Bounds and safety

JSON headless bodies: 8 KiB. Platform bodies: 16 KiB. Token forms and success responses: 1 MiB. Error responses: 64 KiB. One 30-second deadline covers transport/body work and preserves caller cancellation. TerraScaleAuthException exposes HTTP status, safe allowlisted ErrorCode and RetryAfter; unknown codes, producer messages/bodies and serializer details are not retained. Requests do not follow redirects when the documented handler prerequisite is met. The SDK never disposes caller-owned HttpClient. No storage, deployments, credential provisioning or service binaries are part of this contract. API-key metadata has no prefix or last4 field; do not fabricate either for list responses. The complete secret has format `<ULID>_<52 lowercase Crockford characters>` and appears only at creation/rotation.

## I5 requests

Package all `contracts/auth` manifests/fixtures with the SDK candidate and register them for compatibility checking; this slice does not edit csproj or root files. Keep package version 0.0.X (provisional 0.0.3 belongs to I5). Update root README/release notes to point consumers here and distinguish the C# platform client from legacy instance management. Run matching monorepo producer tests against the exact selected SDK K; do not infer parity from SDK fake tests. No dashboard endpoint/fixture composition or pin changes are made here.
