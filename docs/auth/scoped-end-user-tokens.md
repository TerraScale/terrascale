# Scoped end-user access tokens (TS-827)

A customer backend authenticates its own users and asserts their identity to
ts-auth using an existing project API key. No hosted sign-in, tenant user
provisioning, session cookie or HA1 route is required.

## Backend mint contract

The standalone [OpenAPI contract](scoped-end-user-tokens.openapi.yaml) supplements
the platform API contract and declares the `projectApiKey` Bearer security scheme.

```http
POST /v1/terrascale/projects/{project}/end-user-tokens
Authorization: Bearer <whole project API key>
Content-Type: application/json

{"user_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","claims":{"roles":["reader"]}}
```

```json
{"access_token":"<signed JWT>","token_type":"Bearer","expires_in":900}
```

`project` and `user_id` must be canonical uppercase ULIDs. The asserted user ID
need not name a ts-auth account. The project backend is responsible for verifying
its caller and choosing their ID and roles; it must never relay browser-submitted
identity or role fields without authorization. Send only the resulting short-lived
JWT to the browser. Keep the project API key on the backend.

`claims` and `claims.roles` are optional. Roles are case-sensitive ordinal strings:
at most 64 entries, each 1–128 characters, not all whitespace and without control
characters. Duplicates are removed ordinally. **Only roles are supported.** Unknown
members at either level are rejected, including JWT, project, organization,
administrator, database and scope claims. Arbitrary claims have no rule consumer
and would add an unnecessary trust boundary. Bodies must be UTF-8 JSON, at most
16 KiB and four levels deep. The complete signed compact JWT, including its
signature, must fit **8192 ASCII bytes**. Even within the per-role limits, a role
set whose final encoded token exceeds this cap returns `400 invalid_request`;
reduce the roles. The cap applies after JSON/Unicode encoding and signing.

The key must belong to the exact route project, have no database restriction,
and include both `documents:read` and `documents:write`. Read-only, write-only,
database-narrowed and organization keys are refused, as are platform JWTs and
cookies. ts-auth reads the current stored key and constant-time digest, expiry,
project and organization linkage before minting, rechecking immediately before
signing. Deleted projects/organizations and revoked, expired or tampered keys
cannot mint. Existing API-key rotation grace applies.

Every response has `Cache-Control: no-store`. Authentication runs before body
parsing. Missing/invalid authority returns 401 with a Bearer challenge; malformed
input returns 400. Those responses use `{"error":{"code":"…","message":"…"}}`.
Storage admission failures return 503 with the same envelope. Requests share the
OAuth token peer/client IP budget (default 1000/minute), charged before credential
checks, plus a 1000/minute budget per authenticated key. Rate limits return 429,
`Retry-After` and the OAuth envelope
`{"error":"rate_limited","error_description":"…"}`. This is a server-to-server
Bearer endpoint: it accepts no cookie authentication and adds no browser CORS
allowlist or hosted UI flow.

## Signed identity and rule enforcement

| Field | Value |
| --- | --- |
| `typ`, `alg`, `kid` | `at+jwt`, `ES256`, published platform-ring key ID |
| `iss`, `aud` | Configured platform issuer and access-token audiences |
| `sub` | Backend-asserted canonical end-user ULID |
| `token_kind` | `end_user` |
| `instance_id`, `project_id` | The exact authenticated project ULID |
| `roles` | Bounded array of backend-asserted roles, compared ordinally |
| Lifetime | At most 15 minutes, capped at the API key's expiry |

No `scope`, `database`, `org_id` or `platform_role` claim is issued. TerraBase
validates the platform signature, exact issuer and audience, access token type,
lifetime, ULID subject and equality of `instance_id`/`project_id`. It rejects
unknown token kinds, malformed roles, anonymous identities and end-user tokens
carrying administrator or scope/database grant claims.

Authenticated end users enter only document, transaction, shape and database-change
routes. SQL, management, collection metadata and export routes remain forbidden.
The document read/write scopes synthesized by TerraBase admit those routes;
collection rules authorize each data operation and deny when rules are absent.
The identity passed to rules is
`ProjectAccess { IsEndUser=true, Actor=sub, Roles=trusted ordinal set }`.
Anonymous requests remain unauthenticated. Existing project grant tokens keep
their current validation and scope behavior. TerraBase stores no copy of end-user
accounts or role membership.

## Revocation and readiness

Issued JWTs validate **offline**: revoking a key or deleting its project stops
future issuance but does not revoke an already-issued token. Rotated old key
strings may still mint during their configured grace period. Backend user/role
revocation must stop issuance in the customer's backend. An issued token can
remain usable for its remaining 15-minute
lifetime, plus TerraBase's 30-second validation clock skew. Use a shorter key
expiry when a shorter maximum mint lifetime is needed. `expires_in` is the whole
number of seconds remaining at issuance and never exceeds 900.

This route removes the HA1 producer dependency for customer backends. Integration
tests use synthetic keys and the same-commit TerraBase through the SDK. Production
browser proof still requires a deployed customer backend that verifies its users,
delivers minted tokens safely and exercises configured rules. This change creates
no production credentials, provisioning, permission expansion or deployment.
