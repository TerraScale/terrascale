# Guest accounts, browser APIs and device sign-in

> This document describes the integration contract that the C# port restores
> (TS-432, TS-433, TS-437). Routes become available as each stream lands; see
> `docs/parity.md`.

All endpoints below are included in `/openapi/v1.json` and `/scalar`.
Authentication and account storage remain on the existing GRU authority.

## Browser client configuration

Create a public OAuth client with a management key kept on your backend:

```http
POST /v1/example/oauth-clients
Authorization: Bearer sk_...
Content-Type: application/json

{"name":"Consumer app","public":true,"redirect_uris":["https://app.example.com/callback"],"allowed_origins":["https://app.example.com"],"allow_anonymous":true}
```

Existing clients default to `allow_anonymous: false`. Use
`PATCH /v1/example/oauth-clients/{id}` to edit these four settings. Client IDs,
secrets and authentication methods do not change. Public clients use `none`;
confidential clients keep their configured Basic or form authentication.

Origins must be exact, canonical HTTPS origins, without paths, trailing slashes,
wildcards, credentials, query strings or fragments. HTTP loopback origins are
allowed for local development. An instance's allowed origins are the union of
its registered clients' origins. Origin permission does not replace client or
bearer authentication. Cross-origin cookies are not enabled.

CORS covers the token, userinfo, revocation, device authorization and guest APIs.
It does not cover hosted cookie flows or management APIs. Preflights accept
`Authorization` and `Content-Type`, with GET for userinfo and POST for mutations.
Removing an origin affects subsequent requests without a process cache.

## Guest sign-in and conversion

1. POST form field `client_id` to `/example/oauth/anonymous`. Confidential
   clients must also authenticate. The response has OAuth access, refresh and
   ID tokens. JWTs include `is_anonymous: true`, and userinfo includes the flag.
   Store tokens using your platform's secure credential storage.
2. Save application data using the token's `sub` as the user ID. Guest users
   consume normal user quota seats. They have no email/password credential.
3. To convert, POST `{"email":"person@example.com"}` to
   `/example/oauth/anonymous/upgrade`, using the guest access token as Bearer.
   Store the returned `verification_id`. The email contains an eight-digit code
   valid for ten minutes, with at most five verification attempts per challenge.
4. POST `{"verification_id":"gup_...","code":"12345678"}` to
   `/example/oauth/anonymous/upgrade/complete`, with the same guest identity's
   Bearer token. Optionally include `password` (8–1024 bytes) to set a password;
   omitting it creates a verified email/passwordless account.
5. Replace the old token set with the response. `sub`, application metadata and
   quota seat stay unchanged; `is_anonymous` becomes false. The credential epoch
   advances and ts-auth rejects the old access/refresh tokens and sessions.

An application that verifies JWT signatures offline must still enforce token
expiry and its revocation/epoch policy. Offline verification alone cannot learn
that an account changed or a token was revoked.

Hosted “Sign out everywhere” and the management user-session revoke-all endpoint
advance the credential epoch, invalidating outstanding OAuth grants as well as
browser sessions. Revoking one browser session rejects access tokens carrying
its `sid`; independently held refresh families must be explicitly revoked or
invalidated through revoke-all.

The proof is bound to the tenant, user, client and credential epoch. Verification
codes are stored as keyed HMAC digests. An already claimed email causes a 409;
no existing account is replaced or merged. Sign into that account separately.
Disabling guest admission stops new guest creation and upgrade requests. It
does not itself ban existing users or revoke their refresh families.

Guest admission permits 30 requests per client per minute. Upgrade requests
permit five per user and per target-mailbox bucket per minute. These counters
use conditional writes on the authority and do not reset on process restart.
They supplement, rather than replace, edge controls and bot protection. User
quota is the final durable admission boundary. Configure SMTP for actual email
delivery; the development logging sender does not expose verification codes.

The conversion is committed before credentials are returned. If the response is
lost after conversion, sign in using verified email or the chosen password;
do not attempt to reuse the guest token. A failed concurrent or ambiguous
conversion can conservatively retain an email uniqueness claim. Repair only
after fencing that user's writes and verifying ownership and the final User
record; never release a claim merely because a client observed an error.

## Device authorization

1. POST form `client_id` and optional `scope` (`openid profile email`) to
   `/example/oauth/device_authorization` with the client's normal authentication.
2. Show `user_code` and `verification_uri` on the initiating device. The optional
   `verification_uri_complete` pre-fills the code but never approves it.
3. The user signs into the hosted page, verifies the device code, application
   name and scopes, then explicitly approves or denies. The page uses Datastar
   for the approval response, with an ordinary form fallback.
4. Poll `/example/oauth/token` with form fields `client_id`, `device_code`, and
   `grant_type=urn:ietf:params:oauth:grant-type:device_code`.
5. Start with the returned five-second interval. On `authorization_pending`,
   continue at that interval. On `slow_down`, increase it by five seconds for
   every subsequent poll. Stop on `access_denied`, `expired_token` or any other
   terminal error. Codes expire after ten minutes. Successful redemption returns
   the normal token set and cannot be repeated.

Client issuance is limited to 60 requests per minute. Hosted code lookup and
decision attempts share a 20-request-per-user-per-minute limit. Approval is
bound to the browser session; revocation, account credential changes, bans and
SCIM deactivation prevent redemption. Do not log pairing or verification codes.

## Organization permission checks

Management keys need the `roles` scope for `/v1/example/roles` and the
`authorization` scope for `/v1/example/authorization/check` (or `*`).

```json
{"key":"editor","name":"Editor","permissions":["documents:read","documents:write"]}
```

POST that definition to `/v1/example/roles`. Existing membership/invitation
APIs assign the exact role key. GET a role to obtain its `ETag`; PUT or DELETE
requires that value in `If-Match`. Retirement permanently reserves the key and
removes its grants, preventing old memberships from regaining access through
role-name reuse. Empty permission lists are allowed; wildcard grants are not.

```http
POST /v1/example/authorization/check
Authorization: Bearer sk_...
Content-Type: application/json

{"user_id":"usr_...","organization_id":"org_...","permissions":["documents:write"]}
```

The decision requires **all** requested permissions and reads active identity,
organization, membership, SCIM authority and role state. Undefined role labels
grant no application permissions. Hosted administrator role labels and
management-key scopes remain separate. Applications must enforce the returned
decision and their own resource ownership rules; a storage error is not a grant.

## Storage retention

New collections are `organization_roles`, `oauth_device_grants`,
`anonymous_upgrades` and `oauth_flow_limits`. Grant/challenge expiry is enforced
at read time; this service does not yet automatically purge their rows or delete
inactive guests. Establish retention/cleanup against the admitted TerraBase
contract before large-scale deployment. Retired role keys must remain reserved.
