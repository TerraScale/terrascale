# Firebase Authentication feature mapping

Each Firebase Authentication or Identity Platform feature, what TerraScale offers
instead, and what to do when it offers nothing. TerraScale facts come from
[end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication),
[authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities)
(which takes precedence over older lists) and the repository docs and source
linked in each row.

## Sign-in methods

| Firebase | TerraScale | Action |
| --- | --- | --- |
| Email/password ([docs](https://firebase.google.com/docs/auth/web/password-auth)) | Password sign-in, sign-up and reset on the hosted pages. | Import users without a hash; first sign-in by email code or reset. Firebase scrypt hashes cannot be imported. |
| Email link ([docs](https://firebase.google.com/docs/auth/web/email-link-auth)) | Email code sign-in: the user types a code from the mail. | Turn on email codes. Magic-link sign-in is not available. |
| Phone / SMS ([docs](https://firebase.google.com/docs/auth/web/phone-auth)) | Not available. | Ask phone-only users to add and verify an email before cutover; users without one cannot be imported. |
| Anonymous ([docs](https://firebase.google.com/docs/auth/web/anonymous-auth)) | Guest accounts and conversion are not available. | Keep guest data under your own `app_user_id`; attach it after sign-up. |
| Google | Google social provider. | New Web application client; same Google Cloud project. |
| Apple | Apple social provider (Services ID, Team ID, Key ID, `.p8`). | Same developer team so user identifiers stay the same. |
| GitHub | GitHub social provider. | New OAuth App. |
| Generic OIDC `oidc.*` ([docs](https://firebase.google.com/docs/auth/web/openid-connect)) | Generic OpenID Connect provider by its `https` issuer. | One connection named `oidc` per project. |
| Facebook, Twitter/X, Microsoft, Yahoo, Play Games, Game Center | Not in the public provider list (Google, GitHub, Apple, OIDC). | Use generic OIDC where the provider is an OIDC issuer; otherwise move those users to email codes. |
| SAML `saml.*` ([docs](https://firebase.google.com/docs/auth/web/saml)) | Not available for project end users. | Connect the IdP's OIDC interface as the generic OIDC provider, or keep those users on email codes. |
| Custom token sign-in (`signInWithCustomToken`) | No sign-in equivalent on the project issuer. | For data access only, mint scoped end-user tokens on your backend. |

Sign-in matches a linked identity by provider, issuer, client ID and subject;
accounts are never linked by email alone
([social sign-in](https://docs.terrascale.tech/guides/end-user-authentication#what-end-users-see),
[SocialCallbackEndpoint.cs](https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication/Features/SignIn/SocialCallbackEndpoint.cs)):

- A new identity whose provider asserts a verified email creates an account when
  the email is new and sign-up is allowed. When the email already belongs to an
  account, sign-in is refused until the user signs in another way and connects
  the provider from the security page.
- A new identity whose email the provider does not mark verified gets an emailed
  code. Redeeming it connects the provider to the existing account with that
  email, or creates one when sign-up is allowed
  ([project social sign-in](https://github.com/TerraScale/terrascale/blob/main/docs/auth/project-social-sign-in.md)).

## Second factors

| Firebase | TerraScale | Action |
| --- | --- | --- |
| SMS MFA ([docs](https://firebase.google.com/docs/auth/web/multi-factor)) | TOTP authenticator and recovery codes. | Users enroll TOTP; SMS factors cannot move. |
| TOTP MFA ([docs](https://cloud.google.com/identity-platform/docs/web/totp-mfa)) | TOTP and recovery codes. | Users enroll again: the import takes no MFA material. |
| Passkeys | Passkey enrollment and sign-in are documented for TerraScale accounts. | Do not plan end-user passkeys from that row. |

After an end user with MFA signs in with a social provider, the hosted
second-factor step follows; the provider's own MFA does not count.

## Users and profile data

| Firebase field | TerraScale import field | Notes |
| --- | --- | --- |
| `localId` (uid) | `external_id` | At most 255 characters, unique per project. TerraScale assigns its own ULID `id`. |
| `email` | `email` | Required; unique per project. |
| `emailVerified` | `email_verified` | Only verified accounts are imported, with `true`; unverified ones are skipped. |
| `displayName` | `first_name`, `last_name` | 255 characters each; split carefully or keep the whole name in `first_name`. |
| `photoUrl` | `image_url` | Absolute HTTPS URL, at most 2048 characters. |
| `passwordHash`, `salt` | none | Firebase scrypt is not an accepted encoding. |
| `phoneNumber` | `private_metadata` | No phone sign-in; keep it only if the app needs it. |
| `disabled` | none on import | Disable each user after import. |
| `createdAt`, `lastSignedInAt` | `private_metadata` | TerraScale sets its own `created_at`. |
| `providerUserInfo` | none | Provider links cannot be imported; users connect again. |
| Custom claims | `custom_claims` via `PATCH` | Not accepted on import. |

Field rules are in
[user profiles and imports](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md).
Metadata objects are at most 8192 UTF-8 bytes and 10 levels deep:
`public_metadata` reaches the ID token and userinfo with the `profile` scope,
`private_metadata` stays on the backend APIs, and `unsafe_metadata` is writable by
the user and must never drive authorization.

## Custom claims

Firebase custom claims ([docs](https://firebase.google.com/docs/auth/admin/custom-claims))
appear in the ID token as `request.auth.token.<name>`. TerraScale offers two places:

1. **`custom_claims` on the user.** Set only by `PATCH /v1/{project}/users/{user}`
   with an `auth:write` key or a project admin. A JSON object of at most 32
   members and 1000 bytes; names match `[a-z][a-z0-9_]{0,62}`; values are strings
   up to 256 characters, finite numbers, booleans, or arrays of up to 64 strings or
   64 numbers. A sent object replaces the old one; `null` clears it. Every access
   token issued afterwards carries it as `claims`, so changes arrive at the next
   refresh. Users can read their tokens: no secrets.
2. **Roles in backend-minted data tokens.** `POST /v1/terrascale/projects/{project}/end-user-tokens`
   accepts only `claims.roles` (up to 64 strings of 1–128 characters)
   ([scoped end-user tokens](https://github.com/TerraScale/terrascale/blob/main/docs/auth/scoped-end-user-tokens.md)).
   Rules test them with `'admin' IN auth.claims.roles`.

Rename Firebase keys that break the name pattern (`isAdmin` becomes `is_admin`),
turn nested objects into flat keys or role names, and move anything secret to
`private_metadata`. Update every reader in the same release.

## Blocking functions and triggers

| Firebase | TerraScale | Action |
| --- | --- | --- |
| `beforeUserCreated` ([docs](https://firebase.google.com/docs/auth/extend-with-blocking-functions)) | No blocking hook. Sign-up can be turned off for the whole project. | Enforce allowlists in your backend before minting data tokens; react to `user.signed_up` and disable the user. |
| `beforeUserSignedIn` | No blocking hook. | Check your own policy at the callback before creating the app session. |
| `auth.user().onCreate` / `onDelete` | Webhooks `user.created`, `user.signed_up`, `user.deleted`, `user.disabled`, `user.provider_linked` and more. | Subscribe and deduplicate by event id ([webhooks](https://github.com/TerraScale/terrascale/blob/main/docs/auth/webhooks.md)). |

Webhooks are delivered after the change and retried; they cannot refuse it.

## Tokens and sessions

| Firebase | TerraScale |
| --- | --- |
| ID token, 1 hour, RS256, `iss` `https://securetoken.google.com/<project>` ([docs](https://firebase.google.com/docs/auth/admin/verify-id-tokens)) | Access token JWT, 5 minutes, ES256, `iss` `https://auth.terrascale.tech/<project ID>`, `aud` your client ID, `sub` the user id. |
| Refresh token, valid until revoked | Refresh token that rotates on every use and expires at original sign-in plus the project session lifetime (at most 30 days). |
| `verifyIdToken(token, checkRevoked)` | Verify signature with the issuer JWKS, `iss`, `aud`, expiry. Offline checks do not see revocation; tokens last 5 minutes. |
| Session cookies (`createSessionCookie`) | Your own app session after the code exchange. |
| `revokeRefreshTokens` | Disable the user (ends every session at once) or revoke sessions. |

## Security rules

| Firebase (`request.auth`) | TerraBase Rules |
| --- | --- |
| `request.auth.uid` | `auth.uid` (the token `sub`) |
| `request.auth.token.<claim>` | `auth.claims.<name>` |
| `request.auth == null` checks | Requests without a token answer `401`; rules never see them. |
| `resource.data.<field>` | `row.<column>` |
| `request.resource.data.<field>` | `new.<column>` |
| `request.time` | `request.time` (Unix milliseconds; not allowed in `read`) |
| Field validation | `validate` conditions and the `writable` column list |

A database without rules denies every end-user request. A missing claim is NULL
and denies, even under `NOT`
([TerraBase Rules](https://github.com/TerraScale/terrascale/blob/main/docs/terrabase/rules.md)).

## Multi-tenancy

Each Identity Platform tenant has its own users
([multi-tenancy](https://cloud.google.com/identity-platform/docs/multi-tenancy-managing-tenants)).
Each TerraScale project has one user directory, and the same email in two projects
is two unrelated accounts. Use one TerraScale project per tenant, or merge tenants
into one project only when no email appears in two tenants.

## Email

Verification, sign-in and reset mails carry your project name, logo, theme color
and `email_message`, but come from TerraScale's sender with its security copy.
Custom sender domains, your own mail credentials and Firebase-style action URLs
are not supported.
