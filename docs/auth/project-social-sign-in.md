# Project social sign-in

A project's end users can sign in to the project's hosted pages with Google, GitHub, Apple or any OpenID Connect
provider. The customer brings its own OAuth client for each provider; ts-auth stores the client per project and never
shares one across projects. Builds on [project tenant management](project-tenant-management.md).

## Configuration API

Every call takes the caller's terrascale sign-in token, like the other `/v1/{project}/…` calls.

| Call | Role | Effect |
|---|---|---|
| `GET /v1/{project}/settings` | viewer | `providers` lists google, github, apple and oidc, in that order, each with `configured`, its public fields and the `callback_url` to register at the provider. |
| `PUT /v1/{project}/providers/{provider}` | admin | Configures or changes the provider; answers the settings. |
| `DELETE /v1/{project}/providers/{provider}` | admin | Removes the provider and its secret; answers the settings. |

`PUT` body (snake_case JSON):

| Provider | Fields |
|---|---|
| `google`, `github` | `client_id`, `client_secret` |
| `apple` | `client_id` (the Services ID), `client_secret` (the `.p8` private key, an EC P-256 PEM), `team_id`, `key_id` |
| `oidc` | `issuer` (https, no query or fragment), `client_id`, `client_secret` |

`client_secret` is required the first time; omitted, the stored secret is kept. An unknown provider is 404 and an
invalid field 400 `invalid_request`.

## Secrets

The client secret (or Apple's private key) is sealed with `SecretBox` (AES-256-GCM under the OCI Vault master key)
with the project and provider as associated data, so a sealed secret copied to another project or provider does not
open. It lives in the project's `project_auth_settings` record and is never answered, logged or rendered. Audit logs
name the user, project and provider only.

## Hosted sign-in

A project's sign-in and sign-up pages show "Continue with …" for each configured provider.

1. `GET /ui/{project}/social/{provider}?next=…` redirects to the provider with `state`, an OIDC `nonce` and PKCE S256
   (not Apple, which does not support it). The sign-in is sealed into the `tsauth_social_{project}` cookie
   (`SecretBox`, 10 minutes, `Path=/ui/{project}/social`, `Secure`, `HttpOnly`, `SameSite=None` because Apple posts
   the callback cross-site).
2. The provider returns to `/ui/{project}/social/{provider}/callback` (GET; Apple POSTs with `form_post`). The cookie
   must open, be unexpired and carry the returned `state`; it is deleted either way. ts-auth redeems the code with the
   project's secret:
   - Google, Apple and OIDC: the ID token is validated against the issuer's discovery metadata and JWKS (issuer,
     audience = client id, RS256/ES256 signature, expiry, nonce) and must carry `email_verified`. The discovery
     record must name the expected issuer.
   - GitHub: the account's primary email must be verified (`GET /user/emails`, scope `user:email`).
   - Apple: the client secret is a 5-minute ES256 JWT signed with the project's key (`iss` team id, `sub` client id,
     `kid` key id).
3. Sign-in matches a previously linked provider identity (provider, issuer, client ID and subject); accounts are never linked by
   verified email. A provider identity that is already linked signs that account in. A new identity whose email is new to the project
   creates an account when the project allows sign-up. A new identity whose email already belongs to an email/password account is
   refused with 401 and no session; the page tells the user to sign in with their password, then connect the provider from the
   security page.
   A user with MFA continues to the hosted second-factor step. A disabled user, an unverified email or any provider failure answers
   401 with no session. Success sets the project's session cookie and continues to `next` like the other hosted methods.

A signed-in user connects a provider on the hosted security page (`/ui/{project}/security`): the connect is a
same-origin form post from a session opened in the last 10 minutes, the authorization carries a state sealed to that
account, that session and its authentication state for 10 minutes, and the callback binds the identity to that one
account — never by email, never to another account, and not when any account in the project already owns the
identity (the page says the provider account is already connected). Disconnecting a provider on the same page
releases its identities and revokes every prior session and token.

Both pages share the hosted per-IP budget. The outbound client times out after 10 seconds and connects only to
public unicast addresses, so a configured OIDC issuer cannot reach loopback, private, link-local or mesh addresses.

## Tests
