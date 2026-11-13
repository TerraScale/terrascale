# Machine tokens and user-owned API keys

This describes the in-repository API and SDK source. SDK releases are tracked separately in sdk-versions.json.

## Machine applications

Create an OAuth app with POST /v1/{project}/clients using an administrator's platform sign-in token and a fresh create_oauth_client step-up proof. Set type to machine, redirect_uris to [], allowed_scopes to 1–64 unique OAuth scope strings, optional allowed_audiences to up to 64 resource identifiers, and optional token_lifetime_minutes to 5–60 (default 15).

The dashboard's Authentication › OAuth applications page creates and edits machine apps with their scope, audience and lifetime policy.

The client is confidential. The creation response shows client_secret once; storage retains OpenIddict's hash. Existing /clients/{client_id}/secret/rotate rotation applies, including its previous-secret grace. PUT /clients/{client_id} updates scopes, audiences and lifetime without changing app type or secret.

POST form data to /{project}/oauth/token with grant_type=client_credentials, client_id, client_secret, a nonempty scope drawn from the app's allowlist, and optionally one resource from its audience allowlist. HTTP Basic client authentication also works. Interactive apps cannot use this grant.

Tokens are project-issued ES256 JWTs with token_use=machine, sub=client_id, granted scope, the selected resource as aud when supplied, and no end-user identity or auth_time. Verify against the project's discovery document and JWKS with signature, ES256, issuer, expiry and expected audience checks. A client can mint at most 1000 tokens per minute, in addition to the token endpoint's IP budget. Ten failed client secret authentications per client_id exhaust a 15-minute window; even the correct secret is refused until the window ends. Already-issued tokens live until expiry after secret rotation or app deletion; there is no online JWT revocation promise.

Customer APIs must check token_use alongside signature, issuer, audience and expiry: require machine for machine-only endpoints and user for user-only endpoints. Project OAuth user access tokens and backend-asserted end-user tokens carry token_use=user.

## User-owned keys

An administrator or unrestricted project key with auth:write defines allowed scopes using PUT /v1/{project}/user-api-key-policy, with {"allowed_scopes":["invoices:read"]}. An empty list prevents new keys. Changing the list does not change existing keys.

- POST /v1/{project}/users/{user}/api-keys: {"name":"Accounting","scopes":["invoices:read"],"expires_at":null}. Requires project administrator or auth:write.
- GET the same route lists metadata without secrets. Requires viewer or auth:read.
- DELETE /v1/{project}/users/{user}/api-keys/{key} revokes the key. Requires administrator or auth:write.
- /v1/{project}/me/api-keys and its /{key} DELETE route provide the same operations to the end user through their project hosted-session cookie. Mutations require the auth public Origin header. User IDs come from the live session. Creation requires a sign-in within the last 10 minutes; an older live session receives 403 reauthentication_required with a sign-in-again error. Self-service expiry defaults to 90 days and cannot exceed 365 days.
- POST /v1/{project}/users/{user}/sign-out with a project administrator bearer token or auth:write project key signs the user out everywhere, invalidating all their user-owned keys.

In Authentication › Users, select API keys for a user to list their key names, prefixes, scopes and expiry, or revoke a key. Secrets never appear in this list.

IDs are ULIDs. Secrets have a visible tsk_ plus eight random characters, an underscore and 43 random characters. The full secret is returned only at creation and stored as a SHA-256 digest. Never log it. A user can have at most 25 live keys, across self-service and administrator creation. Creation counts unexpired keys at the current auth epoch and stamp and advances the user row version atomically with insertion; concurrent creates cannot exceed the cap. The 26th live key receives 409 user_key_limit. Creation permits at most 100 keys per hour, including replacements after revocation. A unique prefix collision generates a new secret and retries insertion once.

## Backend verification

POST /v1/{project}/api-keys/verify with {"key":"<user-owned-secret>"} and an unrestricted project API key with auth:read as the Bearer credential. Sign-in tokens, organization keys, another project's project key and auth:write alone do not authorize verification.

A valid response is {"valid":true,"user_id":"<ULID>","scopes":["invoices:read"],"key_id":"<ULID>","expires_at":null}. Invalid responses have valid:false and null identity fields. Trust identity and scopes only when valid is true. Enforce your application's required scopes yourself.

Verification reads the project-scoped key and live user on every call and compares digests in constant time. There is no cache: after committed revocation the next request fails. Expired keys and deleted or disabled users fail verification. Every key captures the user's auth epoch and stamp at creation; sign-out everywhere, password reset, disabling or any other authentication-state change invalidates self-service and administrator-created keys immediately, including after re-enabling the user. Another project's user key cannot match. Unavailable storage fails closed with a service error. Each project permits 1000 verification calls per minute; 429 includes Retry-After.

Use the in-repository JavaScript SDK on a backend:

~~~js
const result = await auth.projects.verifyUserApiKey(projectReadKey, projectId, {
  key: suppliedUserKey,
});
if (!result.ok) throw new Error('Verification unavailable');
if (!result.value.valid) throw new Error('Unauthorized');
if (!result.value.scopes.includes('invoices:read')) throw new Error('Forbidden');
const userId = result.value.user_id;
~~~

Create/revoke mutations commit their apiKey.created or apiKey.revoked audit and C42 webhook outbox record atomically with the key. Subscribe webhook endpoints to those names. Payloads identify the user and never contain the key secret.
