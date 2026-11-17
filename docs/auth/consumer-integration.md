# Guest accounts, browser APIs and device sign-in

The served OpenAPI specification is the source for current routes and schemas.

All endpoints below are included in `/openapi/v1.json` and `/scalar`.
Authentication and account storage remain on the existing GRU authority.

## Browser client configuration

A project admin registers the browser app as a public OAuth client of the project, in the dashboard or through the
[project tenant routes](project-tenant-management.md) (`POST /v1/{project}/clients` needs a signed-in person and a
`create_oauth_client` step-up proof):

```http
POST /v1/{project}/clients
Authorization: Bearer <sign-in token>
X-TerraScale-Step-Up-Proof: <proof>
Content-Type: application/json

{"name":"Consumer app","type":"public","redirect_uris":["https://app.example.com/callback"],"allowed_origins":["https://app.example.com"],"allow_anonymous":true}
```

`allowed_origins` defaults to `[]` and `allow_anonymous` to `false`, also for clients created before these fields
existed. `PUT /v1/{project}/clients/{client_id}` (with an `update_oauth_client` proof) changes them; an omitted field
stays unchanged. Client IDs, secrets and authentication methods do not change. Machine apps take neither field.

Each origin is exact and canonical: `https://host[:port]`, or `http` on a loopback host, in lowercase, without a
default port, path, trailing slash, wildcard, userinfo, query string or fragment; at most 10 per client. Anything
else answers `400 invalid_request`. A project's allowed origins are the union of its clients' origins, read from
storage on every request, so removing one affects the next request.

CORS covers the project issuer's token, userinfo, revocation and guest routes (`/{project}/oauth/token`,
`/oauth/userinfo`, `/oauth/revoke`, `/oauth/anonymous`). It does not cover hosted pages, cookie flows, management
APIs or the platform issuer; device authorization is served only by the platform issuer and gets none. A preflight
from an allowed origin answers `204` with `Access-Control-Allow-Origin` set to that origin, `GET` for userinfo or
`POST` for the others, `Authorization, Content-Type` and a 600-second max age; responses expose `Retry-After` and
`WWW-Authenticate`. `Access-Control-Allow-Credentials` is never sent, so no cookie crosses origins. An unknown origin
gets no CORS headers, and the browser refuses the response. Origin permission does not replace client or bearer
authentication. A `429` from the per-address token budget is answered before the project is resolved and carries no
CORS headers, so a browser sees it as a network error.

## Guest sign-in

The [Browser apps with Rules todo walkthrough](../../sdk/js/README.md#browser-apps-with-rules) includes the
one-time table and Rules setup and an executable browser module using `BrowserAuth` and `createTerraBaseClient`.
It signs in a guest, creates, reads and updates its own todo directly, with no app backend or project key.
The same client supports tenant PKCE redirects, hosted magic links, live shapes and replaceable secure storage.

1. POST the form field `client_id` to `/{project}/oauth/anonymous`. The client needs `allow_anonymous`; a
   confidential client also authenticates. `grant_type`, `scope` and other parameters are refused. The response is
   the token endpoint's: access, refresh and ID tokens for the scopes `openid offline_access`. Both JWTs carry
   `is_anonymous: true` and no email; the access token names the project's data audiences like any user sign-in
   ([rules](../terrabase/rules.md) section 3.1), and userinfo answers `{"sub":"…","is_anonymous":true}`.
   Keep tokens in memory or your platform's secure credential storage.
2. Save application data with the token's `sub` as the user ID; rules see `auth.is_anonymous = 1`. A guest is a
   user of the project: it is listed by the users API (with the placeholder email `<id>@anonymous.invalid`, which
   never receives mail and never signs in), counted in `counts.users`, and disabled or deleted like any user.
   It has no email or password credential.
3. Refresh with the normal refresh grant. A guest's sign-in ends at the project's session lifetime after it was
   created; the guest then has no way to sign in again.

Only a project whose `sign_up_mode` is `public` admits guests (`403 access_denied` otherwise). A client without
`allow_anonymous` answers `400 unauthorized_client`. Each client admits 30 guests a minute and each address 10 a
minute per project (`429 rate_limited` with `Retry-After`); the token endpoint's per-address budget also applies.
These counters use conditional writes in storage and survive restarts. ts-auth has no per-project user cap today;
because guests are ordinary user rows, any user quota counts them. Turning `allow_anonymous` off stops new guests
from that client; it does not end existing guests' sign-ins. Converting a guest into an email account is not
available yet.

An application that verifies JWT signatures offline must still enforce token
expiry and its revocation/epoch policy. Offline verification alone cannot learn
that an account changed or a token was revoked.

Hosted “Sign out everywhere” and the management user-session revoke-all endpoint
advance the credential epoch, invalidating outstanding OAuth grants as well as
browser sessions. Revoking one browser session rejects access tokens carrying
its `sid`; independently held refresh families must be explicitly revoked or
invalidated through revoke-all.

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
{"key":"editor","name":"Editor","permissions":["sql:read","sql:write"]}
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

{"user_id":"usr_...","organization_id":"org_...","permissions":["sql:write"]}
```

The decision requires **all** requested permissions and reads active identity,
organization, membership, SCIM authority and role state. Undefined role labels
grant no application permissions. Hosted administrator role labels and
management-key scopes remain separate. Applications must enforce the returned
decision and their own resource ownership rules; a storage error is not a grant.

## Storage retention

Guests are rows of `users` with `is_anonymous` set; client origins are `oidc_applications.allowed_origins`. ts-auth
does not delete inactive guests; disable or delete them through the users API.
