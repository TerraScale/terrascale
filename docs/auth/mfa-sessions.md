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
or social login first establishes a five-minute enrollment challenge. A user
without a factor receives no session or OAuth tokens until they enroll and
confirm an allowed factor on the hosted page. Enrollment reuses the same TOTP
and WebAuthn verifiers as account security. Recovery codes are issued after TOTP
activation; they cannot activate a factor or bypass enrollment.

An enrolled user signs in with their authenticator or a user-verified passkey.
A recovery code is accepted only while another allowed factor remains enrolled.
Changing the allowed factors is checked again before issuing a session or
redeeming a code/refresh token.

Hosted session use and OAuth redemption update the server's last-activity time.
After the inactivity limit, the next hosted or refresh request refuses and
revokes that session. Absolute lifetime still runs from the original sign-in;
neither activity nor step-up extends it. A single-session sign-in atomically
stores a fresh session stamp on the user and the new session. Earlier stamps
remain invalid even if the project later turns single-session mode off.
Across replicas, authorization follows the persisted authentication state and
credential-claim consistency guarantees described in [storage](storage.md).

## Sign out everywhere

`POST /v1/{project}/users/{user}/sign-out` requires a platform sign-in bearer
token and the project admin role. It returns 204, or 404 for an unknown user.
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

`POST /v1/{project}/step-up` accepts a live project sign-in access token and
`{"client_id":"<client ULID>","password":"<password>"}` or a `code` instead of
`password`. The code must be a current allowed TOTP or unused recovery code.
The existing hosted IP, password and factor attempt budgets apply.

The response has `proof` and `expires_at`. The proof is an ES256 JWT with
`typ: step-up+jwt`, the exact project issuer, the client ID as audience,
the user ID as `sub`, a ULID `jti`, fresh Unix-second `auth_time`, `amr`
and an expiry five minutes later. Customer apps verify its signature against
the project's JWKS, type, issuer, audience, subject and expiry. It is a reusable
five-minute verification assertion, not an API access token or a single-use
authorization for a particular customer operation. The next refresh includes the
fresh `auth_time`; its absolute session limit remains unchanged.
