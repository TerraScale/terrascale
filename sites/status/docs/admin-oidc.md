# Status administration authorization

Activation prerequisite: deploy the ts-auth registration described by the sibling
monorepo's `docs/auth/status-public-client.md` (registration commit `78f37618`).
Status must not deploy this change before that registration is live and verified.

The only trusted issuer is `https://auth.terrascale.tech/terrascale`. Discovery,
JWKS and OAuth endpoints use that tenant prefix; root discovery is not a fallback.
The public client is `00000000000000000000000006`, with the exact callback
`https://status.terrascale.tech/admin/callback`. No client secret is needed.

`GET /admin/authorize` starts authorization code with S256 PKCE and `openid`.
The state, nonce and verifier expire after five minutes. Identity tokens require
ES256 signature, exact issuer/client audience, valid lifetime, nonce, and matching
UserInfo subject. The callback also requires the authorization response's issuer.

Every protected request calls
`GET https://auth.terrascale.tech/v1/terrascale/organizations/00000000000000000000000001`
with the sign-in access token. Only that organization's `owner` or `admin` role
allows access. Names, email addresses, project roles and platform operator roles
cannot grant status administration. Lookup failure denies access.

The existing session secret encrypts HttpOnly, Secure, host-only SameSite=Lax
cookies; no plaintext access token or verifier reaches HTML or browser storage.
Session lifetime is bounded by the access token, ID token and one hour. No refresh
token is requested. Old password sessions are invalidated by the cookie format
change. Mutations retain same-origin and session-bound CSRF checks. Sign-out
clears the local status cookie; it does not end the provider's SSO session.

The sign-in control is a navigation link so the page's self-only form policy does
not block an external OAuth redirect. Callback responses suppress referrers.
Tests use signed HTTP fakes; live sign-in, registration and role membership still
require verification after the auth deployment.
