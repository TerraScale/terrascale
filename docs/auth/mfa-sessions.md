# Project MFA and session policies

The project admin sets `configuration` through `PUT /v1/{project}/settings`.
Sending configuration replaces the policy, so read the current settings before
changing individual fields. These values are customer data and are enforced by
ts-auth on hosted pages, session creation, authorization-code redemption and refresh.

| Field | Default | Constraint |
| --- | --- | --- |
| `require_mfa` | false | Requires an enrolled, allowed factor and a verified second factor |
| `allow_totp` | true | Allows authenticator app codes |
| `allow_passkey` | true | Allows user-verified passkeys |
| `inactivity_timeout_minutes` | null (off) | 15 to 43200 minutes |
| `single_session` | false | Each new sign-in supersedes earlier sessions |
| `session_lifetime_seconds` | 1209600 | 300 to 2592000 seconds |

At least one factor must be allowed when MFA is required. A password, email code
or social login first establishes a primary proof. Enrollment uses a separate
five-minute sealed purpose, issued only to users without an allowed factor.
First enrollment requires a fresh inbox code; the password route sends that
code before offering enrollment, including for imported accounts. Its form posts
to /ui/{project}/mfa/enroll/verify-email independently of passcode sign-in.
The five-minute sealed enrollment-inbox challenge binds the user, tenant and
authentication state; only its purpose-specific, single-use code advances to
factor enrollment, without creating a session. Resending uses the same pending
primary proof at /ui/{project}/mfa/enroll/email and preserves the hosted email
cooldown and hourly budgets. A user
without a factor receives no session or OAuth tokens until they enroll and
confirm an allowed factor on the hosted page. Enrollment reuses the same TOTP
and WebAuthn verifiers as account security. Recovery codes are issued after TOTP
activation; they cannot activate a factor or bypass enrollment.

An enrolled user signs in with their authenticator or a user-verified passkey.
A recovery code is accepted only while another allowed factor remains enrolled.
Every successful TOTP or passkey enrollment sends a new-factor notification email.
Turning off allow_totp lets TOTP users sign in with a password alone when
require_mfa is false. Enable required MFA before disabling a factor if those
users must enroll a replacement. Changing the allowed factors is checked again before issuing a session or
redeeming a code/refresh token.

When an inactivity timeout is set, hosted session use and OAuth redemption
update the server's last-activity time at most once per minute. Concurrent
activity updates do not fail authentication. No activity write occurs with
the timeout off.
After the inactivity limit, the next hosted or refresh request refuses and
revokes that session. Absolute lifetime still runs from the original sign-in;
neither activity nor step-up extends it. A single-session sign-in atomically
stores a fresh session stamp on the user and the new session. Earlier stamps
remain invalid even if the project later turns single-session mode off.
Across replicas, authorization follows the persisted authentication state and
credential-claim consistency guarantees described in [storage](storage.md).

Refresh tokens minted with ts_auth_session bind to the session that authorized
their issuance, not the browser's current cookie. Signing in again replaces the
cookie while preserving the earlier session and other applications' refresh
tokens, unless single-session mode supersedes them. Explicit logout or session
revocation still ends tokens bound to that session. Tokens issued before this
binding existed (without the claim) remain valid until their normal expiry;
they cannot be idle-checked or checked against a session stamp. User epoch and
authentication-state revocation still apply, including sign out everywhere.
Refreshing one of these tokens reissues it without the session claim; the
transition ends at the original absolute lifetime at the latest. Idle timeout
and single-session enforcement begin after a new sign-in creates a bound token.

## Sign out everywhere

`POST /v1/{project}/users/{user}/sign-out` requires a platform sign-in bearer
token with the project admin role or a project secret API key with `auth:write`. It returns 204, or 404 for an unknown user.
The operation changes the user's authentication epoch and stamp, invalidating
every hosted session, authorization code, refresh token and pending enrollment
challenge. The user stays active and can sign in again.

The dashboard exposes this on Authentication → Users. All five SDKs expose the
same operation:

| SDK | Method |
| --- | --- |
| .NET | `auth.Projects.SignOutUserAsync(token, project, user)` |
| JavaScript | `auth.projects.signOutUser(token, project, user)` |
| Go | `user.SignOutProjectUser(ctx, project, user)` |
| Python | `auth.sign_out_user(user, project=project)` (sync and async) |
| Rust | `auth.projects().sign_out_user(token, project, user)` |

These methods are source APIs in this change; published package qualification
follows the SDK release. Issued access tokens validated offline continue to work
until their normal expiry; immediate backend revocation requires a live state check.

## Project step-up

[canonical served OpenAPI contract](https://auth.terrascale.tech/openapi/v1.json).

`POST /v1/{project}/step-up` accepts a live project sign-in access token and
`{"client_id":"<client ULID>","password":"<password>"}` or a `code` instead of
`password`. The code must be a current allowed TOTP or unused recovery code.
With required MFA, a password alone is refused. A code or passkey rechecks
the second factor of the live authenticated session. Supply password alongside
code or passkey to prove both factors freshly.

For passkeys, POST the email to /ui/{project}/sign-in/passkey/options from
the hosted origin, use its options with navigator.credentials.get, and send
client_id, its sealed challenge, and the assertion object as passkey to
step-up. The assertion must belong to the access-token subject and is single-use.
The existing hosted IP, password and factor attempt budgets apply.

The response has `proof` and `expires_at`. The proof is an ES256 JWT with
`typ: step-up+jwt`, the exact project issuer, the client ID as audience,
the user ID as `sub`, a ULID `jti`, fresh Unix-second `auth_time`, the bound session ID as `sid`, `amr`
and an expiry five minutes later. Customer apps verify its signature against
the project's JWKS, type, issuer, audience, subject and expiry. It is a reusable
five-minute verification assertion, not an API access token or a single-use
authorization for a particular customer operation. The next refresh includes the
fresh `auth_time`; its absolute session limit remains unchanged.

Authentication methods use RFC 8176: pwd, otp for TOTP/recovery, and
swk, user and mfa for user-verified passkeys (hardware protection is not
asserted): user verification plus possession proves multiple factors. Fresh
password plus a second factor also includes mfa.

The local journey builds the actual pre-binding auth revision f0fa77892f4260f0da5240dbd9172b0c3185c9c4 in isolation and runs it on a separate loopback port against the throwaway stack. It mints an authentic refresh token without the session claim and redeems it at the current service; no token-signing seam is added to production.
