# Custom tokens

A project backend that signs in its own users asserts their identity to TerraBase with a custom token: a short-lived
access token signed by the project's ts-auth tenant, minted with a project API key. No hosted sign-in, tenant user,
session or OAuth client is needed. TerraBase evaluates the database's [rules](../terrabase/rules.md) against it.

## Mint

```http
POST /v1/{project}/custom-tokens
Authorization: Bearer <whole project API key>
Content-Type: application/json

{"user_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","database":"01ARZ3NDEKTSV4RRFFQ69G5FAW","claims":{"roles":["editor"],"plan":"pro"}}
```

```json
{"access_token":"<signed JWT>","token_type":"Bearer","expires_in":300}
```

- `user_id` is a canonical uppercase ULID. It may name a tenant user or not: the backend vouches for it and owns any
  mapping from its own user IDs.
- `database` is optional, a canonical ULID. It restricts the token to that database; any other database answers
  `404 unknown_database`.
- `claims` is optional, with the trusted-claims limits of [rules section 3.3](../terrabase/rules.md#33-trusted-claims):
  a JSON object of at most 32 members and 1000 bytes serialized, names `[a-z][a-z0-9_]{0,62}`, values strings of at
  most 256 characters, finite numbers, booleans, or arrays of at most 64 strings or numbers. Rules read it as
  `auth.claims`; roles are `claims.roles`, which file-bucket role policies also read.
- Unknown members are rejected. Bodies are UTF-8 JSON of at most 16 KiB and four levels deep.

The key belongs to the route's project, has no database restriction and holds `auth:tokens:mint`; it needs no other
scope. Only a project admin can give a key that scope. The key's and the project's IP allowlists apply. A mint-only
key can neither run SQL nor manage ts-auth, yet it can act as any end user of the project, so keep it on the backend
and send only the minted token to the browser. Never relay a browser-supplied user ID or claims without checking them.

| Answer | When |
| --- | --- |
| 200 | Minted. `Cache-Control: no-store`. |
| 400 `invalid_request` | Malformed body, non-canonical ULID, claims over the limits or an unknown member. |
| 401 `unauthorized` | Missing, malformed, revoked or expired key, with a Bearer challenge. |
| 403 `insufficient_scope` | The key lacks `auth:tokens:mint`. |
| 403 `ip_not_allowed` | The key's or project's IP allowlist refuses the caller. |
| 404 | The project is unknown, the key is of another project or organization, or the key is restricted to a database. |
| 429 `rate_limited` | The OAuth token IP budget or the 1000 a minute budget of the key is spent, with `Retry-After`. |
| 503 | Storage is unavailable. |

## Token

| Field | Value |
| --- | --- |
| `typ`, `alg`, `kid` | `at+jwt`, `ES256`, a key of the project tenant's ring |
| `iss` | The project tenant issuer, `https://auth.terrascale.tech/{project}` |
| `aud` | The project's data audiences, `https://api.terrascale.tech/v1/projects/{project}`, and nothing else |
| `sub` | `user_id` |
| `token_use` | `custom` |
| `database`, `claims` | Present when given |
| `iat`, `nbf`, `exp` | Issued now, expires after 5 minutes |

A custom token carries no organization, so `auth.org_id` and `auth.org_role` are NULL, and `auth.is_anonymous` is 0.
TerraBase validates it offline against the tenant's published keys, like a project sign-in token, and refuses a
custom token that names an organization or a guest. It reaches only its own project's routes.

ts-auth's own endpoints refuse it: it carries no ts-auth audience and no session, so userinfo and every OAuth
endpoint answer 401. The platform APIs refuse the tenant issuer.

## Revocation

Tokens are not refreshable; the backend mints again. Revoking or expiring the key, or deleting the project, stops
minting at once. An issued token stays valid for the rest of its 5 minutes plus TerraBase's validation clock skew.
