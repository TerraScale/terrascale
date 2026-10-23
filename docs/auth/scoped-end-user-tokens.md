# Scoped end-user access tokens (TS-827)

`EndUserAccessTokens.IssueAsync(project, session)` accepts only a live ts-auth
session in an existing project's tenant. `ProjectRecord.Id` is that tenant's
identifier; TerraBase stores no copy of end-user accounts or role membership.

The signed access token has this contract:

| Field | Value |
| --- | --- |
| `typ`, `alg`, `kid` | `at+jwt`, `ES256`, published platform-ring key ID |
| `iss`, `aud` | Configured platform issuer and access-token audiences |
| `sub` | Stored tenant user's canonical ULID |
| `token_kind` | `end_user` |
| `instance_id`, `project_id` | The same existing project ULID, taken from the tenant session |
| `roles` | Bounded array of trusted stored user roles, compared ordinally |
| Lifetime | At most 15 minutes, capped at the session's absolute expiry |

No `scope`, `database`, `org_id` or `platform_role` claim is issued. Submitted
project IDs or role names cannot grant authority: the helper resolves the
project and tenant session through ts-auth's authoritative store. Platform,
foreign-tenant, revoked, expired and stale-epoch sessions cannot mint tokens.
Session validation also preserves the existing MFA restrictions.

TerraBase validates the platform signature, exact issuer and audience, access
token type, lifetime, ULID subject and equality of `instance_id`/`project_id`.
It rejects unknown token kinds, malformed roles, guests (`is_anonymous=true`)
and end-user tokens carrying administrator or scope/database grant claims.
Authenticated end users enter only the document, transaction, shape and
database-change routes. SQL, management, collection metadata and export routes
remain forbidden. The document read/write scopes synthesized by TerraBase only
admit those routes; collection rules authorize their data operations and deny
when rules are absent. The identity passed to rules is
`ProjectAccess { IsEndUser=true, Actor=sub, Roles=trusted ordinal set }`.
Anonymous requests remain unauthenticated. Existing project grant tokens keep
their current validation and scope behavior.

Tokens validate offline. Session/role revocation stops new issuance immediately
but an issued token can remain usable for its remaining 15-minute lifetime
(plus TerraBase's 30-second validation clock skew).

## Readiness boundary

This change provides the issuer helper and TerraBase validation, with integration
tests against synthetic identities. It does **not** expose a public mint endpoint
or change HA1-owned hosted sign-in. Project tenants currently serve discovery and
JWKS only; their hosted sign-in and OAuth token flows are not available.

HA1 must provide the tenant sign-in path and a same-origin, rate-limited session
exchange (proposed `POST /v1/{project}/session/token`) before a production browser
can acquire this token. The exchange must enforce configured Origin/CSRF checks,
live tenant session validation and `Cache-Control: no-store`; this helper alone
is not a public HTTP authentication boundary. No credential provisioning,
production permission expansion or deployment is included. Browser production
proof remains blocked on that producer and the scoped-model confirmation.
