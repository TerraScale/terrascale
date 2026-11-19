# Agent identities

An agent is an AI agent that is its own principal in a project tenant, or in the platform tenant `terrascale` for a
TerraScale account ([TerraScale accounts](#terrascale-accounts)). A user of that tenant owns it, and it holds its own
public key. The agent creates its key pair and keeps the private key; ts-auth stores only the public key. The model
follows the AgentID idea: a stable agent id, an accountable owner and key-based proof of possession.

This page covers the management API, self-enrollment with owner approval, the end user's own agent list, how
an agent gets a token, and agents of TerraScale accounts with the dashboard's token exchange. Agents of TerraScale
accounts can also sign in to any app through [Sign in as agent](agentid-provider.md).

## Agent policy

An administrator, or an unrestricted project key with `auth:write`, sets the scopes agents may hold:

```http
PUT /v1/{project}/agent-policy
{"allowed_scopes":["invoices:read","invoices:write"]}
```

`GET` on the same route returns it (viewer or `auth:read`). The list holds 0 to 64 unique scopes. An empty list, or
no policy, prevents new agents. Changing the list does not change existing agents.

## Agents

| Operation | Route | Authorization |
| --- | --- | --- |
| Create | `POST /v1/{project}/users/{user}/agents` | admin or `auth:write` |
| List a user's agents | `GET /v1/{project}/users/{user}/agents` | viewer or `auth:read` |
| List agents | `GET /v1/{project}/agents?owner={user}&limit=&after=` | viewer or `auth:read` |
| Read one | `GET /v1/{project}/agents/{agent}` | viewer or `auth:read` |
| Add a key | `POST /v1/{project}/agents/{agent}/keys` | admin or `auth:write` |
| Revoke a key | `DELETE /v1/{project}/agents/{agent}/keys/{kid}` | admin or `auth:write` |
| Revoke the agent | `DELETE /v1/{project}/agents/{agent}` | admin or `auth:write` |

Project keys must be unrestricted project secret keys, as on every project management route. Lists use the cursor paging of the other project lists: `limit` 1 to 100 (default 50) and `after` set to the
previous page's `next`. `?owner=` only filters the project's agents: a well-formed id of a user with no agents there,
or of no user in the project, answers an empty page. `GET /v1/{project}/users/{user}/agents` answers `404` for a user
that is not in the project.

Create:

```json
{
  "name": "Invoice agent",
  "allowed_scopes": ["invoices:read"],
  "allow_self_subject": false,
  "expires_at": null,
  "public_jwk": {"kty": "EC", "crv": "P-256", "x": "<43 base64url characters>", "y": "<43 base64url characters>"}
}
```

- `name` is 1 to 64 characters. `allowed_scopes` is 1 to 64 unique scopes, each in the agent policy.
- `allow_self_subject` (default `false`) lets a later token grant name the agent itself as the subject instead of
  acting for its owner.
- `expires_at` is optional and must be in the future.
- The owner must be an enabled user of the project.

The answer (`201`):

```json
{
  "id": "01KAEXAMPLE0000000000000001",
  "project_id": "01KAEXAMPLE0000000000000000",
  "owner_user_id": "01KAEXAMPLE0000000000000002",
  "name": "Invoice agent",
  "status": "active",
  "allowed_scopes": ["invoices:read"],
  "allow_self_subject": false,
  "keys": [{"kid": "01KAEXAMPLE0000000000000003", "thumbprint": "<RFC 7638 SHA-256 thumbprint>", "created_at": "2026-11-19T12:00:00Z", "revoked_at": null}],
  "created_at": "2026-11-19T12:00:00Z",
  "expires_at": null,
  "revoked_at": null,
  "last_used_at": null
}
```

There is no secret in any answer. `last_used_at` is the last time the agent got a token (below), written at most once a
minute.

## Enrollment

An agent (a CLI, a script) can ask to be enrolled itself; the project user who will own it approves in the browser. The
agent generates its key pair first and never sends the private key.

1. Start, unauthenticated:

   ```http
   POST /v1/{project}/agent-enrollments
   {"name":"Invoice agent","requested_scopes":["invoices:read"],"allow_self_subject":false,"public_jwk":{"kty":"EC","crv":"P-256","x":"…","y":"…"}}
   ```

   `name`, `requested_scopes` and `public_jwk` follow the create rules above (the scopes are checked against the agent
   policy only at approval). The answer (`201`):

   ```json
   {"enrollment_id":"01KAEXAMPLE0000000000000004","user_code":"WDJB-MJHT","verification_uri":"https://auth.terrascale.tech/ui/{project}/agent-enrollments","verification_uri_complete":"https://auth.terrascale.tech/ui/{project}/agent-enrollments?user_code=WDJB-MJHT","expires_in":600,"interval":5}
   ```

   Starts are limited to 10 per IP per 15 minutes and 300 per project per hour (`429` with `Retry-After`). An unknown
   project is `404`.
2. The agent shows `user_code` and `verification_uri` (or opens `verification_uri_complete`) to its user, together with
   the first 16 characters of its key's RFC 7638 thumbprint.
3. The user signs in on the project's hosted pages, which carry the project's branding, and sees the agent's name,
   the requested scopes the agent policy allows, whether it asks to act as itself, the key fingerprint, the code and
   the signed-in account, and asks the user to check that the agent shows the same code. Opening the page never decides. **Deny** ends the request. **Approve** first asks for a fresh
   verification (password or emailed code, then the authenticator code when MFA is on) through the hosted step-up page
   with purpose `approve_agent`, bound to this enrollment; the user returns and approves. Approval creates the agent
   owned by that user exactly as `create_agent` does (limits, policy check, audit and `agent.created`), with the
   allowed requested scopes, and consumes the step-up proof in the same save.
4. The agent polls no faster than `interval`:

   ```http
   POST /v1/{project}/agent-enrollments/{enrollment_id}/token
   {"proof":"<ES256 JWT>"}
   ```

   The proof is signed by the enrolled private key: header `alg` `ES256`, claims `aud` (this exact URL), `jti`, `iat`
   and `exp` at most 300 seconds ahead. Every accepted poll's `iat` must be later than the previous one's, so a
   proof works once. Without such a proof the answer is `401 invalid_proof` and reveals nothing about the enrollment.
   Otherwise the answer is `400` with `authorization_pending`, `slow_down` (sooner than `interval` after the last
   poll), `access_denied` or `expired_token`, or `200 {"agent_id":"…","kid":"…"}` once approved. The result is answered
   once; later polls answer `expired_token`.

Codes and races:

- `user_code` is 8 letters from `BCDFGHJKLMNPQRSTVWXZ` (no vowels or look-alike digits), shown as `XXXX-XXXX`; case,
  spaces and the dash are ignored. Only its SHA-256 is stored, unique per project. It works for 600 seconds and only
  while the request is undecided; a code of another project is never found.
- Each code lookup on the page charges the hosted IP budget and 20 per user per 15 minutes.
- Approve and deny write the enrollment row at the version they read, and so does a poll that is admitted and answers
  `authorization_pending` or the result; a poll refused with `invalid_proof`, `slow_down`, `access_denied` or
  `expired_token` writes nothing. Of two concurrent decisions one wins and the other answers that the request
  changed; a poll that loses to a decision answers `slow_down`. After approval the agent has another 600 seconds to
  collect its id.
- Approval is bound to what the page showed: the form carries an HMAC (keyed from the master key) over the project,
  the enrollment, the signed-in user and the allowed scopes it listed. A form rendered for another account, or a
  different allowed set (the agent policy changed), shows the page again for a fresh decision; the step-up proof must
  belong to the same signed-in user. The approval save also asserts the agent
  policy, the approver's user row and live session, and is refused when the enrollment, the step-up proof or the
  session expired before it is submitted.
- Each code lookup is charged to the stored hosted IP budgets (and the IPv6 /48 site budget) and a stored per-user
  budget before the code is read, and each start to its stored per-IP and per-project budgets; when that storage fails
  the request is refused, never admitted from a replica's memory.
- A project's start asserts the project row's version in the same save as the new enrollment, so a start racing the
  project's closure or deletion fails (`409 version_conflict`, then `404`) instead of leaving a row behind its deletion
  cleanup.
- The session pruner deletes enrollments on a later sweep once they have been expired for 600 seconds.

## Your agents

The signed-in end user of a project manages the agents they own with the project's hosted session cookie, like
`/me/api-keys`:

| Operation | Route |
| --- | --- |
| List | `GET /v1/{project}/me/agents?limit=&after=` |
| Revoke | `DELETE /v1/{project}/me/agents/{agent}` |

Mutations need the `Origin` of the auth public origin (`403` otherwise). Without a live session the answer is `401`.
The user comes from the session, never the request; another user's agent is `404`. Revoking behaves as the
management revoke.

## Keys

- A key is a public P-256 EC JWK: `kty` `EC`, `crv` `P-256`, and `x` and `y` as unpadded base64url of 32 bytes each,
  a point on the curve. `kid`, `key_ops` and `ext` are ignored; `alg` must be `ES256` and `use` must be `sig` when
  present. Any other member is `400 invalid_jwk`.
- A JWK with a private member (`d`, `p`, `q`, `dp`, `dq`, `qi`, `oth` or `k`) is `400 private_key_material` and is
  never stored or logged.
- ts-auth stores only `{"crv","kty","x","y"}` and its RFC 7638 SHA-256 thumbprint. A thumbprint is unique among the
  key rows a project keeps, explicitly revoked keys included: such a key is `409 agent_key_in_use`. Deleting the owner
  deletes their agents' key rows, which frees those thumbprints.
- An agent has at most 2 active keys (`409 agent_key_limit`). Rotate by adding the new key, switching the agent over,
  then revoking the old one. Revoking a key keeps it listed with `revoked_at`; revoking it again is `204`.
- Adding a key to an agent that is not active or has expired is `409 agent_inactive`.

## Limits and errors

- A user has at most 25 live agents (`409 agent_limit`): active, unexpired and created at the owner's current
  authentication state (below). Revoked agents and agents of an earlier sign-in state do not count.
- Creation is limited to 100 agents per user per hour (`429`).
- Concurrent writes that lose a race answer `409 version_conflict`; repeat the request.
- An id from another project, or a user or agent that does not exist there, is `404`.

## What makes an agent unusable

An agent is usable only when its `status` is `active`, it has not expired, and its owner check passes. The status
in every answer already includes the owner check.

| Event | Effect |
| --- | --- |
| `DELETE /v1/{project}/agents/{agent}` | `status` becomes `revoked` with `revoked_at`, and every active key gets `revoked_at`. The row stays. Revoking again is `204` with no change. |
| Signing the owner out everywhere, a password reset or email change, or any other change of the owner's authentication state | The owner check fails: the agent captured the owner's authentication epoch and stamp at creation and they no longer match. The agent answers `status: revoked` with `revoked_at: null`; nothing reactivates it. |
| Disabling the owner | The owner check fails (disabling also advances the owner's authentication state, so enabling the owner again does not bring the agent back). |
| Deleting the owner | The owner's agents and their keys are deleted with the user (foreign keys with `ON DELETE CASCADE`), so the agent is `404`. |
| Deleting the project | The project's agents, keys, enrollments and policy are deleted with it. |

## Getting a token

An agent signs in headlessly with its own key (RFC 7523 JWT bearer grant). It signs an assertion with an active key and
posts it to the project token endpoint (`token_endpoint` in `/{project}/.well-known/openid-configuration`, which lists
the grant in `grant_types_supported`):

```http
POST /{project}/oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&assertion=<JWT>&scope=invoices:read&requested_subject=owner
```

The assertion is an ES256 JWS (no other algorithm, no encryption) with header `kid` set to the key's `kid` and no
`jwk`, `jku`, `x5u`, `x5c` or `crit` member (the key is always the registered one), and claims:

| Claim | Value |
| --- | --- |
| `iss`, `sub` | the agent id |
| `aud` | the token endpoint URL exactly, `https://auth.terrascale.tech/{project}/oauth/token`, as the only audience |
| `iat`, `exp` | JSON integers of Unix seconds (a fraction, exponent or string is refused, never rounded), `iat` before `exp`, `exp - iat` at most 300 s; `iat` not in the future and `exp` not past, each within 30 s of clock skew |
| `jti` | 1 to 128 characters, unique per agent: a used `jti` is refused until the assertion has expired |

`scope` (optional) is a space-separated subset of the agent's `allowed_scopes`; without it the token gets all of them.
`requested_subject` (optional) is `owner` (default) or `agent`. No other parameter is accepted (`400 invalid_request`),
and there is no client authentication.

The answer is a token response with `access_token`, `token_type: Bearer`, `expires_in` (300, or less when the agent's `expires_at` comes
sooner: a token never outlives its agent) and `scope`; there is no
refresh token and no ID token. The access token is an ES256 JWT (`typ: at+jwt`) signed by the project's key ring under
the project issuer, verifiable against the project JWKS:

| Claim | Delegated (`requested_subject=owner`) | Self (`requested_subject=agent`) |
| --- | --- | --- |
| `sub` | the owner's user id | the agent id |
| `act` | `{"sub": "<agent id>"}` | absent |
| `owner_sub` | absent | the owner's user id |
| `token_use`, `actor_type` | `agent`, `agent` | `agent`, `agent` |
| `agent_id`, `scope` | the agent id, the granted scopes | the same |

`requested_subject=agent` needs `allow_self_subject: true` on the agent. The token has no `aud`, so it never carries a
project data audience, and ts-auth's userinfo and platform endpoints refuse it too because it carries no
user authentication state. Customer APIs check it as
[machine tokens and user-owned API keys](machine-tokens-and-user-api-keys.md) describes.

Every refusal is `400 invalid_grant` with the same description, whatever the cause: an unknown or revoked key, a bad
signature or algorithm, a wrong `iss`, `sub` or `aud`, a lifetime outside the limits, a replayed `jti`, an agent that
is not usable (above: revoked, expired, owner disabled, deleted or signed out everywhere since the agent was created), a
scope outside `allowed_scopes`, or a self subject the agent does not allow. Each agent may get 60 tokens a minute and
each owner's agents 600 together (`429 rate_limited` with `Retry-After`), on top of the token endpoint's address
budget. Only a request that passes every check above is charged, so a revoked key or a refused scope never spends the
budget its agent's live keys share. A replayed assertion passes those checks and fails only at the final save, so
replay attempts still consume the agent's and owner's rate budget.

ts-auth signs the token first and releases it only when one save commits. That save inserts the `jti`, writes
`last_used_at` when it is due, and asserts the versions of the key and the owner it checked. The save is the point
that orders issuance against revocation: revoking the agent rewrites every active key, revoking a key rewrites that
key, and a sign-out everywhere or a disable rewrites the owner, so any of them that commits before the save makes it
fail and no token leaves ts-auth. Agent expiry is time, not a row change: right before the save ts-auth checks the
assertion's and the agent's deadlines again on a fresh clock, and the token's `exp` is never later than the agent's
`expires_at`, so a token issued in the last moment before expiry still ends with the agent. A concurrent token request of the same agent that also writes `last_used_at` can
lose that save and get `invalid_grant`; its `jti` was not used, so it may send the same assertion again. A token
already issued stays valid until it expires (at most 5 minutes); there is no online revocation of agent tokens.

## Lifecycle events

Each mutation commits its audit entry and its lifecycle event in the same save as the change. Subscribe endpoints to
these names as for the other end-user lifecycle events.

| Event | Trigger | `data` |
| --- | --- | --- |
| `agent.created` | An agent is created | `user_id` (owner), `agent_id`, `agent_key_id` (its first key) |
| `agent.revoked` | An agent is revoked | `user_id`, `agent_id` |
| `agent.key_added` | A key is added to an agent | `user_id`, `agent_id`, `agent_key_id` |
| `agent.key_revoked` | A key is revoked on its own | `user_id`, `agent_id`, `agent_key_id` |

Revoking an agent emits only `agent.revoked`, not one `agent.key_revoked` per key. The owner check failing, and user
deletion, emit the user events they already emit (`session.revoked`, `user.disabled`, `user.deleted`), not agent
events.

## TerraScale accounts

A TerraScale account, the account you sign in to the dashboard with, can own agents too: a coding agent then calls
the TerraScale MCP server with its own short-lived token instead of an API key. These agents live in the platform
tenant `terrascale` and use the same rules and code as project agents, with these differences:

| | Project agents | TerraScale account agents |
| --- | --- | --- |
| Agent policy | Set per project by its administrators | Fixed by TerraScale: `mcp:read` and `mcp:write`. No route reads or changes it. |
| Enrollment | `POST /v1/{project}/agent-enrollments` | `POST /v1/terrascale/agent-enrollments`; the approval page is `https://auth.terrascale.tech/ui/terrascale/agent-enrollments`, signed in with the TerraScale account, with the same step-up (`approve_agent`) |
| Collecting the result | `POST /v1/{project}/agent-enrollments/{id}/token` | `POST /v1/terrascale/agent-enrollments/{id}/token` |
| Token | `POST /{project}/oauth/token`, project issuer | `POST /terrascale/oauth/token`; issuer `https://auth.terrascale.tech/terrascale`, keys at `/terrascale/.well-known/jwks.json` |
| Your agents | `/v1/{project}/me/agents` with the hosted session cookie | `GET /v1/terrascale/me/agents`, `DELETE /v1/terrascale/me/agents/{agent}` and `PATCH /v1/terrascale/me/agents/{agent}` (`share_owner_email`, see [Sign in as agent](agentid-provider.md#owner-email)) with a platform sign-in access token |
| Management API | `/v1/{project}/agents`, `/v1/{project}/users/{user}/agents` | None: an account agent is created only by enrollment and managed only by its owner |
| Audit and events | An audit entry in the project's organization and lifecycle events | A ts-auth log line per change (`Platform agent audit: <action> <target> by <actor>`); no lifecycle events |

Starts are limited to 10 per IP per 15 minutes and 300 an hour for the whole platform tenant. The token grant, the
assertion rules, the limits, and what makes an agent unusable are the same as above. ts-auth's platform API does not
accept an agent token itself; the dashboard exchanges it for its owner (below).

An agent enrolling and getting a token:

```http
POST https://auth.terrascale.tech/v1/terrascale/agent-enrollments
{"name":"Coding agent","requested_scopes":["mcp:read","mcp:write"],"public_jwk":{"kty":"EC","crv":"P-256","x":"…","y":"…"}}

201 {"enrollment_id":"01KAEXAMPLE0000000000000004","user_code":"WDJB-MJHT","verification_uri":"https://auth.terrascale.tech/ui/terrascale/agent-enrollments","verification_uri_complete":"https://auth.terrascale.tech/ui/terrascale/agent-enrollments?user_code=WDJB-MJHT","expires_in":600,"interval":5}

POST https://auth.terrascale.tech/v1/terrascale/agent-enrollments/01KAEXAMPLE0000000000000004/token
{"proof":"<ES256 JWT, aud this URL>"}

200 {"agent_id":"01KAEXAMPLE0000000000000001","kid":"01KAEXAMPLE0000000000000003"}

POST https://auth.terrascale.tech/terrascale/oauth/token
grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&assertion=<JWT, aud https://auth.terrascale.tech/terrascale/oauth/token>&scope=mcp:read

200 {"access_token":"<agent token>","token_type":"Bearer","expires_in":300,"scope":"mcp:read"}
```

### Agent token exchange

Only the ts-dashboard client exchanges an account agent's delegated token for an access token of its owner, so that
the dashboard can act for the owner within the owner's own permissions. It is a token endpoint grant of the platform
issuer, shaped like RFC 8693:

```http
POST https://auth.terrascale.tech/terrascale/oauth/token
Authorization: Basic <ts-dashboard client id and secret>
Content-Type: application/x-www-form-urlencoded

grant_type=urn:terrascale:params:oauth:grant-type:agent-exchange&subject_token=<agent token>&subject_token_type=urn:ietf:params:oauth:token-type:access_token
```

- Client authentication is required (`client_secret_basic` or `client_secret_post`), and only the ts-dashboard client
  holds the grant. Another client answers `400 unauthorized_client`; missing or wrong client authentication `401 invalid_client`.
- `subject_token_type` must be `urn:ietf:params:oauth:token-type:access_token`. Any other parameter, or a missing
  `subject_token`, is `400 invalid_request`.
- The subject token must be signed by the platform key ring (`kid` of `/terrascale/.well-known/jwks.json`), with ES256,
  `typ` `at+jwt`, `iss` `https://auth.terrascale.tech/terrascale`, no `aud`, not expired (no clock skew),
  `token_use` and `actor_type` `agent`, `agent_id`, `act.sub` equal to `agent_id`, `sub` the owner and no `owner_sub`.
  A self-subject token, a project issuer's agent token and a user's sign-in token are refused.
- ts-auth then reads the agent: it must be active and unexpired, owned by `sub`, hold an active key, and its owner must
  be enabled and have the authentication state the agent captured (no sign-out everywhere, password reset or email
  change since).
- Every refusal of the subject token or the agent is `400 invalid_grant` with the same description.

ts-auth reads the agent and its owner from the home region, and the save that stores the new token's entry asserts
both rows' versions. When either changed in between (a revocation, sign-out everywhere or disable committed), the
entry is stored revoked and the answer is `400 invalid_grant`, so no token minted after such a change leaves ts-auth. The answer is a
token response with `access_token`, `token_type: Bearer` and `expires_in`: 300 seconds, or less. The signed `exp` is
capped at the subject token's `exp` when the token is generated, and a subject token that expired by then is refused,
so the owner token never outlives it. There is no refresh token, no ID token and no scope. The access token is the
owner's platform sign-in access token, with:

| Claim | Value |
| --- | --- |
| `sub` | the owner |
| `act`, `agent_id` | `{"sub": "<agent id>"}`, the agent id |
| `aud` | the platform issuer and `TS_AUTH_ACCESS_TOKEN_AUDIENCES`, as a sign-in |
| `auth_time` | the agent's `created_at`, when the owner approved it with a fresh verification |
| `ts_auth_epoch`, `ts_auth_state` | the owner's current authentication state |
| `token_use` | `user` |

It never carries `ts_auth_mfa` or `platform_role`. So an organization that requires two-factor authentication refuses
it, and an owner who has an authenticator turned on gets no access to the platform API through an agent. It never
carries scopes either: the dashboard reads the agent's `mcp:read` and `mcp:write` from the agent token it exchanged.

The owner token is checked again on every use. ts-auth's platform API, `/terrascale/oauth/userinfo` and the
organization and project token exchange (every OpenIddict validation of a token carrying `agent_id`) read the agent
named by `agent_id` from the home region on each call, and refuse the token once the agent is revoked or expired, its
last key is revoked, or its owner signed out everywhere, was disabled or deleted. So a token issued in the instant
before a revocation commits is refused on its first use after it. An organization or project token already issued
from it stays valid until it expires (60 seconds), and an agent token until its own expiry (at most 5 minutes), but
neither can be exchanged again.

## Storage

`agents`, `agent_keys`, `agent_policies`, `agent_enrollments` and `agent_assertions` ([storage](storage.md)). `agent_assertions` holds each
used `(project_id, agent_id, jti)` as its primary key. A row is kept until 10 minutes after the assertion stops being
accepted (`exp` plus the 30 s skew). That margin outlasts the 10 s token request deadline and any clock difference
between replicas, so no request that accepted the assertion can still be saving when its row is pruned. Session pruning
deletes those rows every 10 minutes, and the rows go with their agent. An agent's primary key is
`(project_id, id)`; a key's is `(project_id, kid)` with a foreign key to its agent. `project_id` always references
`projects`: the platform tenant's agents and enrollments reference the hidden `terrascale` project row
([platform bootstrap](platform-bootstrap.md#platform-tenant-row)), and every query names the tenant.

- Creation counts live agents and rewrites the owner's row version in the same save, so two creations that both
  read 24 live agents cannot both commit; it also asserts the agent policy version.
- Adding a key counts active keys and rewrites the agent's row version in the same save, so two additions that both
  read one active key cannot both commit. It also asserts the owner's row version, so a sign-out everywhere or disable that commits
  after the owner check makes the addition fail instead of adding a key to an unusable agent. Revoking a key or the agent also rewrites the agent's row version, so a
  key cannot be added to an agent that is being revoked.
