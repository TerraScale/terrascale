# Sign in as agent

Any app can offer "Sign in as agent" as a custom OpenID Connect provider and learn which AI agent is signing in and
who is accountable for it. The agents are those of TerraScale accounts ([agent identities](agent-identities.md#terrascale-accounts)):
the account approved the agent and its key, and can revoke it at any time.

The provider is its own issuer, apart from the platform issuer (`/terrascale`) and every project issuer:

| | |
| --- | --- |
| Issuer | `https://auth.terrascale.tech/agents` |
| Discovery | `https://auth.terrascale.tech/agents/.well-known/openid-configuration` |
| Authorization | `https://auth.terrascale.tech/agents/oauth/authorize` (GET or form POST) |
| Token | `https://auth.terrascale.tech/agents/oauth/token` |
| UserInfo | `https://auth.terrascale.tech/agents/oauth/userinfo` (GET or form POST) |
| JWKS | `https://auth.terrascale.tech/agents/.well-known/jwks.json` |
| Flow | Authorization code with PKCE (`S256`) only; no implicit, hybrid, refresh or other grant |
| Scopes | `openid` (required), `profile`, and `owner_email` for registered clients |
| Signing | ES256 with the issuer's own keys, created on first use and stored encrypted under the master key |

## Set up your app

An app is either an open client, with nothing to register, or a registered client with a secret. Only a registered
client may ask for the owner's email.

### Open clients

The `client_id` is the canonical https origin of your app, for example
`https://app.example.com`: lowercase ASCII host (punycode, `xn--…`, for an international name), no userinfo, path,
trailing slash or default port. The client is public: it has no secret and authenticates with PKCE. The
`redirect_uri` can be any https URL on exactly that origin (not a subdomain, not another port); anything else is
refused before anything is shown. A request without the `openid` scope is answered `invalid_scope` at the
`redirect_uri`.

Every OIDC client library works with these settings:

| Setting | Value |
| --- | --- |
| Issuer or discovery URL | `https://auth.terrascale.tech/agents` |
| Client ID | your app's origin, such as `https://app.example.com` |
| Client secret | none (token endpoint authentication `none`) |
| Scopes | `openid profile` |
| PKCE | `S256`, required |
| Response type | `code` |

- **Auth.js**: add a provider with `type: "oidc"`, `id: "agent"`, `name: "Sign in as agent"`,
  `issuer: "https://auth.terrascale.tech/agents"`, `clientId: "https://app.example.com"`,
  `client: { token_endpoint_auth_method: "none" }`, `checks: ["pkce", "state", "nonce"]` and
  `authorization: { params: { scope: "openid profile" } }`.
- **Clerk, Supabase, Auth0** and other hosted auth services: add a custom OpenID Connect provider (Clerk: custom
  OIDC provider; Supabase: a third-party OIDC provider your app completes with its own code exchange and
  `signInWithIdToken`; Auth0: an enterprise OpenID Connect connection on the front channel) with the discovery URL
  above, your origin as client ID, scopes `openid profile` and PKCE on. A service that requires a client secret
  needs a registered client.

### Registered clients

A TerraScale account registers a confidential client with a platform sign-in access token:

```http
POST /v1/terrascale/agentid-clients
Authorization: Bearer <platform sign-in access token>
X-TerraScale-Step-Up-Proof: <create_oauth_client proof>
Content-Type: application/json

{"name":"Example app","redirect_uris":["https://app.example.com/auth/callback"]}
```

```http
HTTP/1.1 201 Created
Location: /v1/terrascale/agentid-clients/01JD6Z8V4M8Q2W3E4R5T6Y7U8K

{"client_id":"01JD6Z8V4M8Q2W3E4R5T6Y7U8K","name":"Example app","redirect_uris":["https://app.example.com/auth/callback"],
 "created_at":"2026-11-19T12:00:00+00:00","client_secret":"<shown once>"}
```

`name` is 1 to 64 characters and is what the agent and the waiting page see as `client_name`. `redirect_uris` holds 1
to 10 distinct absolute https URLs in canonical form (lowercase scheme and host, no default port, userinfo or
fragment); the authorization request's `redirect_uri` must equal one of them exactly. The `client_id` is a ULID. The
`client_secret` is answered once; ts-auth keeps only its SHA-256 digest and compares digests in constant time. An
account has at most 10 clients (`409 client_limit`).

| Operation | Route | Step-up proof |
| --- | --- | --- |
| Register | `POST /v1/terrascale/agentid-clients` | `create_oauth_client` |
| List (no secrets) | `GET /v1/terrascale/agentid-clients` | none |
| Rotate the secret | `POST /v1/terrascale/agentid-clients/{client_id}/secret/rotate` with optional `{"grace_seconds": 0-86400}` | `rotate_oauth_client` |
| Delete | `DELETE /v1/terrascale/agentid-clients/{client_id}` | `delete_oauth_client` |

The proof goes in `X-TerraScale-Step-Up-Proof` ([step-up proofs](step-up-proof.md)). Rotation answers the new
`client_secret` once and `previous_secret_expires_at`: the previous secret keeps working for `grace_seconds` (default
3600), then is refused like any wrong secret; rotating again inside the grace replaces the previous secret, and
`grace_seconds: 0` ends it at once. A deleted client's next authorization request, redemption and secret are refused.
Only the account itself manages its clients: another account's client is `404`, and a token an agent acts with is
`403 owner_required`. A disabled or deleted account's clients stop working with it.

| Setting | Value |
| --- | --- |
| Issuer or discovery URL | `https://auth.terrascale.tech/agents` |
| Client ID | the registered `client_id` |
| Client secret | the registered `client_secret`, sent with `client_secret_basic` or `client_secret_post` |
| Scopes | `openid profile owner_email`, or any subset with `openid` |
| PKCE | `S256`, required |
| Response type | `code` |

Verify the ID token as usual: signature against the JWKS (ES256), `iss` exactly the issuer, `aud` your client ID,
`exp`, and `nonce` when you sent one.

## Claims

ID token claims:

| Claim | When | Value |
| --- | --- | --- |
| `iss`, `aud`, `iat`, `exp`, `jti` | always | issuer, your client ID, times (ID and access tokens live 5 minutes), token id |
| `sub` | always | the agent, pairwise: the same for one agent and one client ID, unrelated across client IDs |
| `actor_type` | always | `agent` |
| `nonce` | when sent | the authorization request's `nonce` |
| `name` | `profile` | the agent's name, chosen by its owner and the same for every client ID |
| `owner_sub` | `profile` | the accountable TerraScale account, pairwise for your client ID |
| `owner_email`, `owner_email_verified` | `owner_email`, registered clients, while the owner shares it | the owner's TerraScale email and whether it is verified |

Claims that are not granted are left out, never `null`. Nothing reveals the agent's or the owner's TerraScale ids, and
only `owner_email` reveals the owner's email. Two apps cannot link their subjects: each pairwise value is an HMAC under a key derived from the master key,
over the client ID and the id. `name` is profile data, not an identifier: apps that both ask for `profile` see the same
name and can use it to correlate an agent across them.

UserInfo answers the same `sub`, `actor_type`, `name`, `owner_sub`, `owner_email` and `owner_email_verified` (as
granted and shared) for the access token, and reads the agent and its owner again on every call. The access token is encrypted and is only good at this UserInfo endpoint; no other
TerraScale issuer or API accepts it, and this issuer accepts no token of another issuer.

### Owner email

The `owner_email` scope is for registered clients only: an open client that asks for it is answered `invalid_scope`
at its `redirect_uri`. A registered client granted it gets `owner_email` and `owner_email_verified` only while the
agent's owner shares their email for that agent; otherwise both are left out, with no error. Sharing is off for every
agent until its owner turns it on, per agent, in the dashboard (Profile › Agents, "Share my email with apps") or with
a platform sign-in access token of the account itself (a token an agent acts with is `403 owner_required`):

```http
PATCH /v1/terrascale/me/agents/{agent}
Content-Type: application/json

{"share_owner_email": true}
```

The answer is the agent, with `share_owner_email`; `GET /v1/terrascale/me/agents` lists it too. Consent is the
owner's and covers every registered client the agent signs in to, whichever account registered it; the agent itself
chooses which client it signs a proof for. ID tokens carry the email as of the redemption, which reads the flag in
the same save that consumes the code; UserInfo reads it on every call, so turning sharing off hides the email from
UserInfo at once, and ID tokens already issued keep it until they expire (5 minutes).

## How an agent completes the sign-in

1. The app sends the agent to the authorization URL. The agent opens it headlessly with
   `Accept: application/json` (a browser gets an HTML page that reloads every 2 seconds and goes to the app once the
   proof is in):

   ```http
   HTTP/1.1 200 OK
   Link: <https://auth.terrascale.tech/agents/v1/authorize/{txn}/proof>; rel="agent-proof"

   {"status":"pending","transaction_id":"{txn}","proof_url":"https://auth.terrascale.tech/agents/v1/authorize/{txn}/proof",
    "client_id":"https://app.example.com","client_name":"https://app.example.com","expires_at":"2026-11-19T12:05:00+00:00"}
   ```

   The transaction is one-time and lives 5 minutes. The page carries the proof URL in `data-agent-proof` too.
2. The agent signs only for a sign-in it started itself: it takes the expected app origin and the transaction from
   its own initiating request, checks that `client_id` and `transaction_id` match them, then posts a proof signed by
   one of its active keys:

   ```http
   POST /agents/v1/authorize/{txn}/proof
   Content-Type: application/json

   {"proof":"<ES256 JWT>"}
   ```

   Header: `alg` ES256, `kid` the key's kid, no `jwk`, `jku`, `x5u`, `x5c` or `crit`. Payload: `iss` and `sub` the
   agent id, `aud` exactly the proof URL, `txn` the transaction id, `client_id` the app's client ID, a `jti` used
   once, integer `iat` and `exp` at most 300 seconds after `iat`. The answer is `204`. Any other proof answers the
   same `401 invalid_proof` and reveals nothing; an unknown, expired or used transaction answers
   `404 expired_transaction`.
3. The agent (or the reloading browser page) requests the same authorization URL again. It redirects once to the
   `redirect_uri` with `code`, `state` and `iss`; the app redeems the code at the token endpoint with its
   `code_verifier` within 60 seconds. Requesting the URL after that answers `400 expired_transaction`.

The proof binds the agent's key to one transaction of one client. A proof for another transaction, another client or
another URL, or a replayed `jti`, is refused. The signed bindings prove which request the agent approved; that it is
the session the agent meant to approve comes from the agent's own context. An app that relays another app's
transaction gets a proof only from an agent that signs a `client_id` it did not start a sign-in with.

## Revocation

The agent, the key that signed the proof and the owner are checked when the proof arrives, again when the code is
issued (in the same write that uses the transaction), again when the code is redeemed (their row versions asserted
in the same save that consumes the code), and on every UserInfo call. The code and the encrypted access token carry
that key's id, so another active key of the same agent does not keep a sign-in alive. A revoked or expired agent, a
revoked proof key, or an owner who was disabled, deleted or signed out everywhere since the agent was created
cannot complete a sign-in, and UserInfo refuses its tokens with `401 invalid_token`. ID tokens already issued stay
valid until they expire (5 minutes).

## Limits

Authorization: 120 requests a minute per caller address and 600 per client ID. Proofs: 30 a minute per agent and
600 per client ID. Token: 60 redemptions a minute per agent and 600 per client ID. Proof and token POSTs also charge
the caller address's shared token budget, as `/terrascale/oauth/token` does. Over a limit the answer is `429` with
`Retry-After`.

## Storage

Registered clients live in `agentid_clients`: the id, the owning account (deleted with it), the name, the redirect
URIs and the SHA-256 digests of the current and previous secret, with the previous one's expiry.


Transactions live in `agentid_transactions` ([storage](storage.md)): a random id, and a unique `request_hash`, the
SHA-256 of the authorization request's parameters, so the same request finds the same transaction. A proof records
the agent, owner and key in the same write as the used `jti` (`agent_assertions`), asserting the key and owner
versions, after rechecking the transaction's deadline and the proof's accepted window; completing the request
rechecks the deadline on a fresh read and sets `completed_at` in a write that asserts the transaction, key and owner
versions, so it happens once, before the deadline, and never after a revocation that won. Rows are pruned an hour
after they expire; a request repeated later opens a new transaction with a new id and proof URL.
