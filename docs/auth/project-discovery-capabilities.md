# Project OIDC discovery capabilities

Project discovery at `/{project_id}/.well-known/openid-configuration` describes
authorization code with S256 PKCE and refresh tokens. Supported scopes are
`openid`, `email`, `profile` and `offline_access`. The advertised authorization,
token, userinfo, revocation, logout and JWKS URLs belong to that project's issuer.

Project discovery omits the device authorization endpoint and its client
authentication methods, along with platform-only grants. Device authorization,
client credentials, token exchange and the GitHub Actions workload grant remain
platform capabilities advertised by `/terrascale/.well-known/openid-configuration`.

This corrects previously overbroad project metadata. It does not enable or remove
runtime grants, change client permissions or SDK DTOs, or change stored records.
Clients should use their project's discovery document for customer-app sign-in.
