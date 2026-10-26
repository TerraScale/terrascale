# Current authentication capabilities and protocol contracts

Reviewed 2026-10-26 against the C# source and anonymous production metadata.
This matrix corrects availability claims in older design/port documents; it
does not certify complete OAuth, OIDC or federation interoperability. A served
route and an integration test are evidence of implementation, not proof that
every deployment or external provider has completed live qualification.

## Availability and scope

`T` below means `terrascale` or an existing project ULID. Project identities,
clients and issuers are separate from platform accounts. Protocol routes live
under `/{T}`, hosted pages under `/ui/{T}`, and management APIs under `/v1`.

| Capability | Current surface and scope | Source / integration evidence |
| --- | --- | --- |
| Email code, password, verified sign-up and password reset | Hosted `/ui/{T}/sign-in`, `/sign-in/verify`, `/sign-in/password`, `/sign-up`, `/sign-up/verify`, `/reset`, `/reset/complete`. Project account policy applies. | `Features/SignIn/SignInEndpoints.cs`; Fast `Oidc/HostedSignInTests.cs` |
| TOTP and recovery codes | Hosted `/ui/{T}/security`, `/security/totp`, `/security/totp/confirm`, `/security/totp/disable`, `/sign-in/mfa`. Recovery codes are an alternative second factor, not a standalone primary login. | `Features/SignIn/MfaEndpoints.cs`; Fast `Oidc/MfaIssuanceTests.cs` |
| Social sign-in | Project Google, GitHub, Apple and generic OIDC providers require provider registration and verified email. Platform linked-provider flow is separate. | `Features/ProjectTenants/SocialProviders.cs`, `Features/SignIn/SocialSignIn.cs`; Fast `Oidc/SocialSignInTests.cs` |
| Organization SSO | OIDC for a DNS-verified email domain; organization owner configures it at `/v1/terrascale/organizations/{organization}/sso`. | `Features/Sso/PutSsoConnectionEndpoint.cs`, `SsoSignIn.cs`; Fast `Management/OrganizationSsoTests.cs` |
| Authorization code and refresh | `/{T}/oauth/authorize`, `/oauth/token`; code with S256 PKCE, registered redirect URI and client permissions. Project issuers serve these grants only. | `Features/Oidc/OidcServer.cs`, `OidcEndpoints.cs`, `Tenancy.cs`; Fast `Oidc/ProjectTenantTests.cs`, `OidcFlowTests.cs` |
| Device authorization | Platform CLI: `POST /terrascale/oauth/device`, browser verification at `/ui/terrascale/device`, polling at `/terrascale/oauth/token`. Not `/oauth/device_authorization`, and not a project grant. | `Features/Oidc/DeviceFlow.cs`; Fast `Oidc/DeviceFlowTests.cs` |
| Machine/API-key credentials and scoped exchange | Platform token endpoint; separately authorized service clients, organization/project API keys and user organization/project exchanges. Not project-issuer grants. | `Features/Platform/PlatformGrants.cs`; Fast `Oidc/PlatformRoleTests.cs`, `TokenContractTests.cs` |
| Discovery, keys, userinfo, revocation and logout | `/{T}/.well-known/openid-configuration`, `/.well-known/jwks.json`, `/oauth/userinfo`, `/oauth/revoke`, `/oauth/logout`. | `Features/Oidc/OidcServer.cs`, `OidcEndpoints.cs`; Fast `Oidc/TenantIssuerTests.cs`, `OidcTokenTests.cs` |
| Passkeys / WebAuthn | Not implemented in this C# surface. Future work has a separate owner; do not publish a usable route or walkthrough. | No endpoint registration; TS-1112 |
| Magic-link sign-in | Not implemented. An email template named `magic_link` does not establish an authentication flow. | `Features/Email/AuthEmails.cs` versus `Features/SignIn/SignInEndpoints.cs` |
| Token introspection | No configured or served introspection endpoint. Use the issuer's JWKS with full JWT validation for offline consumers. | `Features/Oidc/OidcServer.cs`; production discovery has no `introspection_endpoint` |
| Guests and guest conversion | Staged design only; anonymous sign-in/upgrade routes are absent from current source and served OpenAPI. | Historical `consumer-integration.md` examples are not an available API |
| SAML | Staged design only. A record field or protocol constant is not an implementation; connection creation currently requires `oidc`. | `Features/Sso/PutSsoConnectionEndpoint.cs` rejects other protocols |

Source paths in the evidence column are relative to
`src/TerraScale.Authentication`; Fast paths are relative to
`tests/TerraScale.Authentication.Fast`. Existing coverage has been inspected;
it was not executed for this documentation change. Required CI remains the gate.

## Client authentication and protocol limits

Token and revoke accept a public registered client's form `client_id` without
a secret, or a confidential client's `client_secret_basic` / `client_secret_post`.
Public does not mean arbitrary grants or anonymous users. Client registration,
grant permissions, token ownership, redirect and PKCE validation still apply.
OpenAPI therefore declares HTTP Basic **or** an empty security requirement;
the latter also covers form-body client authentication, which OpenAPI security
schemes cannot express. It does not permit confidential clients to omit proof.
Project client creation is `/v1/{project}/oauth-clients` with `type` set to
`public` or `confidential`, not the historical instance-management example.

Discovery is server metadata, not a promise that any client can use every listed
grant or authentication method. For example, discovery advertises
`private_key_jwt`; the project client creation API does not provision client
assertion keys. Only `code` responses and S256 PKCE are supported; do not infer
implicit/password grants from the presence of a password form. `prompt=none`
can check an existing session. Currently `login`, `consent` and `select_account`
return their corresponding interaction errors rather than performing those
interactions, even though production metadata lists those prompt values.

Refresh tokens rotate; persist a successor securely and stop using its
predecessor. On `invalid_grant`, restart sign-in rather than retrying a consumed
credential. Project refresh expiry is bounded by original authentication and
project session policy. A requested subset narrows the new access token; the
replacement refresh token retains the original authorized scope under RFC 6749
section 6. Omitting scope requests that original set, and a response equal to an
explicit requested set may omit `scope`. Rotation also has a finite ancestry
proof budget: the maximum lifetime is not a guaranteed rotation duration. See
[refresh scope and lifetime](regional/refresh-scope-lifetime.md).
Explicit revocation ends that OAuth sign-in at ts-auth;
offline JWT consumers still need expiry and their own revocation policy.
Ordinary OIDC access tokens last at most five minutes; API-key and scoped data
tokens have separate lifetimes and audiences. ID tokens are not API credentials.

### Replay policy qualification dependency

The approved security policy requires a genuine authorization-code replay with
valid client/redirect/PKCE binding to revoke issued descendants. Implementation
and integration qualification are pending in the parent hardening lane. This
document makes no claim that this policy currently succeeds in production.
Do not describe replay as harmless or promise immediate revocation to offline
JWT validators. Publish a concrete replay guarantee only after required CI and
the parent's live qualification establish the resulting behavior. Refresh-token
replay semantics also require reconciliation with their separate security owner.

## Browser and staged-example contract

ts-auth currently registers no cross-origin API CORS policy. Anonymous GETs to
production discovery and OpenAPI with an `Origin` header returned no
`Access-Control-Allow-Origin`. Hosted same-origin forms and browser navigation
are separate from cross-origin `fetch`. The `allowed_origins`, `allow_anonymous`
and guest-upgrade walkthrough in `consumer-integration.md` is staged material,
not supported configuration. No CORS widening is included here. TerraBase's
separate browser API policy does not grant CORS access to ts-auth.

## Anonymous before-change evidence and correction process

On 2026-10-26, unauthenticated GETs returned 200 for:

- `https://auth.terrascale.tech/terrascale/.well-known/openid-configuration`:
  device endpoint `/terrascale/oauth/device`; no introspection endpoint.
- `https://auth.terrascale.tech/openapi/v1.json`: token/revoke security was only
  `[{"clientBasic":[]}]`; device security was `[{}]`. No introspection, anonymous
  guest or `device_authorization` route was present.
- `https://docs.terrascale.tech/guides/end-user-authentication`: public clients
  were already instructed to send `client_id`, contradicting Basic-only OpenAPI.

These GETs do not prove authenticated token issuance or replay handling. No
account, email or production state was created. The source correction represents
token/revoke alternatives without changing server-side client authentication.

When older shared documents conflict, link this matrix instead of silently
promoting their examples to supported behavior. Record the exact route, source,
anonymous metadata and integration evidence in a focused correction. Add a new
shared supplement and an index link; keep public customer docs free of internal
storage, topology and unresolved security findings. The public canonical page
is `/guides/authentication-capabilities` in ts-docs; rollout verification and
ticket state remain with the parent lane.
