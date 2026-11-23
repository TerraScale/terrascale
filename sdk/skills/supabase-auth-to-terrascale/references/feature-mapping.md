# Supabase Auth features mapped to TerraScale

One row per Supabase Auth feature: the TerraScale equivalent, or the gap and its workaround, and where each
statement comes from. "Not available" means the public TerraScale documentation says so or does not offer it;
plan around it, do not wait for it.

## Sign-in methods

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| Email and password ([Supabase][sb-passwords]) | Passwords on the hosted pages; existing bcrypt hashes import and upgrade to Argon2id at the next sign-in | [end-user auth][eua], [user profiles][profiles] |
| Email OTP code ([Supabase][sb-passwordless]) | Email codes on the hosted pages | [end-user auth][eua], [capabilities][cap] |
| Magic link ([Supabase][sb-passwordless]) | Not available. Use email codes: same mailbox proof, typed instead of clicked | [capabilities][cap] |
| Phone and SMS OTP, WhatsApp ([Supabase][sb-phone]) | Not available, and import needs an email. Collect an email in the app before the cutover; users who never give one cannot be migrated | [capabilities][cap], [served OpenAPI][openapi] (`email` is required) |
| Anonymous sign-in ([Supabase][sb-anon]) | Not available for setup ("guest accounts and conversion"). Do not migrate anonymous users; let their data expire or ask them to sign up before the cutover | [capabilities][cap] |
| Google, GitHub, Apple ([Supabase][sb-social]) | Same three providers; new OAuth client at the provider with the TerraScale callback URL | [end-user auth][eua] |
| Other OIDC-capable providers: Azure (Entra ID), Keycloak, GitLab, LinkedIn (OIDC), WorkOS | Generic OpenID Connect by its `https` issuer. One `oidc` connection per project | [end-user auth][eua] |
| Facebook, Discord, Twitter (X), Slack, Spotify, Twitch, Notion, Zoom, Figma, Kakao, Bitbucket | Not available. Those users sign in with an email code (or password) | [end-user auth][eua] |
| Sign in with ID token (native Google or Apple SDK, `signInWithIdToken`) | Not available: the project issuer serves only authorization code and refresh token grants. Use the system browser with the hosted pages | [capabilities][cap] |
| Enterprise SSO with SAML 2.0 ([Supabase][sb-saml]) | Not available. If the IdP also speaks OIDC, use the generic OIDC provider. Organization SSO on docs.terrascale.tech is for TerraScale accounts, not your end users | [capabilities][cap], [single sign-on][sso] |
| Identity linking, manual and automatic ([Supabase][sb-linking]) | Users connect or disconnect providers on the hosted security page. Never automatic by email | [end-user auth][eua] |
| Password reset (`resetPasswordForEmail`) | Hosted password reset | [capabilities][cap] |
| Reauthentication (`reauthenticate`) | No API for your own forms; connecting a provider on the hosted security page needs a sign-in from the last 10 minutes | [end-user auth][eua] |
| Invite user by email (`inviteUserByEmail`) | Create the user without a password (`POST /v1/{project}/users`, no email is sent) and send your own invitation pointing to the hosted sign-in | [user profiles][profiles] |

## Multi-factor authentication

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| TOTP factor ([Supabase][sb-mfa]) | TOTP second factor with recovery codes on the hosted pages. Factors cannot be imported. Import these users without a password hash so a password alone never opens their account; they sign in with an email code and enroll TOTP again | [capabilities][cap], [served OpenAPI][openapi] (import has no factor field) |
| Phone (SMS) factor | Not available. Users switch to TOTP | [capabilities][cap] |
| WebAuthn factor | Passkeys are documented for TerraScale accounts only. Plan TOTP for end users | [capabilities][cap] |
| `aal` claim, `aal2` enforcement in RLS | No documented equivalent claim. The hosted pages run the second factor for users who enrolled; enforce "must have MFA" in your own product flow | [end-user auth][eua] |

## Tokens, sessions and claims

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| Access token JWT, HS256 secret or asymmetric keys ([Supabase][sb-jwt]) | ES256 access token JWT from the project issuer, 5 minutes, verified with the issuer JWKS | [end-user auth][eua] |
| `sub` (UUID), `role`, `aud: authenticated` ([Supabase][sb-jwt-fields]) | `sub` (ULID), `aud` = your client ID, `iss` = project issuer; no `role` claim | [end-user auth][eua] |
| `email`, `phone`, `user_metadata`, `app_metadata` claims | `email` with the `email` scope; `public_metadata` in userinfo and ID token with `profile`; `custom_claims` as `claims` in access tokens | [end-user auth][eua], [user profiles][profiles] |
| Custom access token hook ([Supabase][sb-hooks]) | No hook. Store the values as `custom_claims` (at most 32 members, 1000 bytes) and update them from your backend; they reach tokens at the next refresh | [user profiles][profiles] |
| Refresh tokens, session settings ([Supabase][sb-sessions]) | Rotating refresh tokens; absolute session lifetime 5 minutes to 30 days, default 14 days | [end-user auth][eua] |
| Single session per user, inactivity timeout | No documented equivalent; only the absolute lifetime | [end-user auth][eua] |
| `signOut({ scope: 'global' })` | Admin: disable the user or revoke sessions (`listSessions`, `revokeSession`); user: hosted sign-out plus revocation | [end-user auth][eua] |
| supabase-js session in `localStorage` | Keep tokens out of browser persistent storage; server session or memory | [end-user auth][eua] |

## Users and metadata

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| `auth.users.id` (UUID) | New ULID per user; store the UUID in `external_id` (unique per project, 255 characters) | [user profiles][profiles] |
| `raw_user_meta_data` | `unsafe_metadata` (user-editable, untrusted) plus `first_name`, `last_name`, `image_url` | [user profiles][profiles] |
| `raw_app_meta_data` | `private_metadata` (backend only) and, for keys used in authorization, `custom_claims` | [user profiles][profiles] |
| `email_confirmed_at` | `email_verified` on import (default `false`) | [user profiles][profiles] |
| `banned_until` | Disable or enable the user; a ban until a date needs your own job to re-enable | [end-user auth][eua] |
| Soft delete (`deleted_at`) | Delete removes the account; the address can sign up again. Skip soft-deleted users | [end-user auth][eua] |
| Admin API (`auth.admin.*`) | `/v1/{project}/users` routes and the SDK project clients (`listUsers`, `disableUser`, `deleteUser`, `exportUsers`) | [end-user auth][eua] |
| Triggers on `auth.users`, Postgres `auth` schema queries | Not available: users are not rows in your database. Upsert your profile row on first authenticated request | [rules][rules] |
| Send email hook, custom SMTP, email templates ([Supabase][sb-templates]) | Branding only (name, logo, color, 500-character `email_message`); TerraScale's sender and security copy; no custom sender domain or credentials | [end-user auth][eua] |
| Send SMS hook | Not available (no phone sign-in) | [capabilities][cap] |
| Password verification hook, MFA verification hook | Not available. Attack protection and timing equalization are built in | [user profiles][profiles] |
| Database webhooks on `auth.users` | No publicly documented user lifecycle webhooks. Run your side effects in your backend after sign-in or after the admin call | [end-user auth][eua] |

## Redirects and clients

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| Site URL and redirect allowlist with wildcards ([Supabase][sb-redirects]) | 1 to 10 exact redirect URIs per OAuth application, `https` or loopback `http`; optional project allowlist of up to 100 exact URLs. No wildcards | [end-user auth][eua] |
| Anon key in the browser | Public OAuth application (no secret) | [end-user auth][eua] |
| Service role key on the server | Confidential OAuth application for sign-in; project API key for data | [end-user auth][eua], [authentication][auth] |

## Row access

| Supabase Auth | TerraScale | Source |
| --- | --- | --- |
| `auth.uid()` in RLS ([Supabase][sb-rls]) | `auth.uid` in TerraBase rules (the token's `sub`) | [rules][rules] |
| `auth.jwt() ->> '<claim>'` | `auth.claims.<name>` from `custom_claims` | [rules][rules] |
| `auth.jwt() ->> 'is_anonymous'` | `auth.is_anonymous` exists in rules; anonymous sign-in is not available for setup, so it is 0 for migrated users | [rules][rules], [capabilities][cap] |
| `auth.role()` | No role claim; any end-user token is a signed-in user | [rules][rules] |
| Server-side bypass with the service role | Project API keys are not limited by rules; filter by the verified `sub` in your code | [rules][rules] |

## Product gaps

One line each, with the workaround used by this skill.

- Import cannot keep Supabase UUIDs as user ids: store them in `external_id` and rewrite foreign keys from a map.
- Import cannot carry linked social identities: those users sign in with an email code once, then connect the provider.
- Import cannot carry MFA factors: those users are imported without a password hash, sign in with an email code and enroll TOTP again.
- No route sets or clears a password hash after import: a delta pass deletes and re-imports every user who changed after the export, until it finds none.
- Password sign-in does not require `email_verified`: never import a hash for an unconfirmed address.
- No phone or SMS sign-in: collect emails before the cutover.
- No anonymous or guest accounts for setup: do not migrate anonymous users.
- No magic-link sign-in: email codes instead.
- No SAML and one generic OIDC connection per project: OIDC-capable IdPs only.
- Social providers limited to Google, GitHub, Apple and generic OIDC: other providers' users fall back to email codes.
- No auth hooks: `custom_claims` maintained by your backend.
- No custom email sender or templates: branding and `email_message` only.
- No publicly documented user lifecycle webhooks: side effects in your backend.
- No wildcard redirect URIs: fixed callback hosts for preview deployments.
- No ID token sign-in grant for native SDKs: system browser with the hosted pages.
- No password grant and no browser API to ts-auth: custom sign-in forms become hosted-page redirects.
- The bulk import route is in the served OpenAPI and repository docs, not yet on docs.terrascale.tech.

[eua]: https://docs.terrascale.tech/guides/end-user-authentication
[cap]: https://docs.terrascale.tech/guides/authentication-capabilities
[auth]: https://docs.terrascale.tech/guides/authentication
[sso]: https://docs.terrascale.tech/guides/single-sign-on
[openapi]: https://auth.terrascale.tech/openapi/v1.json
[profiles]: https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md
[rules]: https://github.com/TerraScale/terrascale/blob/main/docs/terrabase/rules.md
[sb-passwords]: https://supabase.com/docs/guides/auth/passwords
[sb-passwordless]: https://supabase.com/docs/guides/auth/auth-email-passwordless
[sb-phone]: https://supabase.com/docs/guides/auth/phone-login
[sb-anon]: https://supabase.com/docs/guides/auth/auth-anonymous
[sb-social]: https://supabase.com/docs/guides/auth/social-login
[sb-saml]: https://supabase.com/docs/guides/auth/enterprise-sso/auth-sso-saml
[sb-linking]: https://supabase.com/docs/guides/auth/auth-identity-linking
[sb-mfa]: https://supabase.com/docs/guides/auth/auth-mfa
[sb-jwt]: https://supabase.com/docs/guides/auth/jwts
[sb-jwt-fields]: https://supabase.com/docs/guides/auth/jwt-fields
[sb-hooks]: https://supabase.com/docs/guides/auth/auth-hooks
[sb-sessions]: https://supabase.com/docs/guides/auth/sessions
[sb-templates]: https://supabase.com/docs/guides/auth/auth-email-templates
[sb-redirects]: https://supabase.com/docs/guides/auth/redirect-urls
[sb-rls]: https://supabase.com/docs/guides/database/postgres/row-level-security
