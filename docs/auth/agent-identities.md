# Agent identities

An agent is an AI agent that is its own principal in a project tenant. A project user owns it, and it holds its own
public key. The agent creates its key pair and keeps the private key; ts-auth stores only the public key. The model
follows the AgentID idea: a stable agent id, an accountable owner and key-based proof of possession.

This page covers the management API. ts-auth does not issue tokens to agents yet.

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
previous page's `next`.

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

There is no secret in any answer. `last_used_at` stays `null` until agents can sign in.

## Keys

- A key is a public P-256 EC JWK: `kty` `EC`, `crv` `P-256`, and `x` and `y` as unpadded base64url of 32 bytes each,
  a point on the curve. `kid`, `key_ops` and `ext` are ignored; `alg` must be `ES256` and `use` must be `sig` when
  present. Any other member is `400 invalid_jwk`.
- A JWK with a private member (`d`, `p`, `q`, `dp`, `dq`, `qi`, `oth` or `k`) is `400 private_key_material` and is
  never stored or logged.
- ts-auth stores only `{"crv","kty","x","y"}` and its RFC 7638 SHA-256 thumbprint. A thumbprint is unique in a
  project, revoked keys included: a key that was ever registered there is `409 agent_key_in_use`.
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
| Deleting the project | The project's agents, keys and policy are deleted with it. |

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

## Storage

`agents`, `agent_keys` and `agent_policies` ([storage](storage.md)). An agent's primary key is
`(project_id, id)`; a key's is `(project_id, kid)` with a foreign key to its agent.

- Creation counts live agents and rewrites the owner's row version in the same save, so two creations that both
  read 24 live agents cannot both commit; it also asserts the agent policy version.
- Adding a key counts active keys and rewrites the agent's row version in the same save, so two additions that both
  read one active key cannot both commit. Revoking a key or the agent also rewrites the agent's row version, so a
  key cannot be added to an agent that is being revoked.
