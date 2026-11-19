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
| Scopes | `openid` (required) and `profile` |
| Signing | ES256 with the issuer's own keys, created on first use and stored encrypted under the master key |

## Set up your app

There is nothing to register. The `client_id` is the https origin of your app, for example
`https://app.example.com` (lowercase, no path, no trailing slash, no default port; `http` only on `localhost`,
`127.0.0.1` or `[::1]`). The client is public: it has no secret and authenticates with PKCE. The `redirect_uri` can
be any URL on that same origin; any other origin is refused before anything is shown.

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
  needs a registered client (see Coming next).

Verify the ID token as usual: signature against the JWKS (ES256), `iss` exactly the issuer, `aud` your client ID,
`exp`, and `nonce` when you sent one.

## Claims

| Claim | When | Value |
| --- | --- | --- |
| `iss`, `aud`, `iat`, `exp`, `jti` | always | issuer, your client ID, times (ID and access tokens live 5 minutes), token id |
| `sub` | always | the agent, pairwise: the same for one agent and one client ID, unrelated across client IDs |
| `actor_type` | always | `agent` |
| `nonce` | when sent | the authorization request's `nonce` |
| `name` | `profile` | the agent's name, as its owner approved it |
| `owner_sub` | `profile` | the accountable TerraScale account, pairwise for your client ID |

Claims that are not granted are left out, never `null`. Nothing reveals the agent's or the owner's TerraScale ids or
email. Two apps cannot link their subjects: each pairwise value is an HMAC under a key derived from the master key,
over the client ID and the id.

UserInfo answers the same `sub`, `actor_type`, `name` and `owner_sub` (as granted) for the access token, and reads
the agent again on every call. The access token is encrypted and is only good at this UserInfo endpoint; no other
TerraScale issuer or API accepts it, and this issuer accepts no token of another issuer.

## How an agent completes the sign-in

1. The app sends the agent to the authorization URL. The agent opens it headlessly with
   `Accept: application/json` (a browser gets the same as an HTML page that reloads every 2 seconds):

   ```http
   HTTP/1.1 200 OK
   Link: <https://auth.terrascale.tech/agents/v1/authorize/{txn}/proof>; rel="agent-proof"

   {"status":"pending","transaction_id":"{txn}","proof_url":"https://auth.terrascale.tech/agents/v1/authorize/{txn}/proof",
    "client_id":"https://app.example.com","client_name":"https://app.example.com","expires_at":"2026-11-19T12:05:00+00:00"}
   ```

   The transaction is one-time and lives 5 minutes. The page carries the proof URL in `data-agent-proof` too.
2. The agent checks that `client_id` is the app it means to sign in to, then posts a proof signed by one of its
   active keys:

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
3. The agent (or the browser page) requests the same authorization URL again. It redirects once to the
   `redirect_uri` with `code`, `state` and `iss`; the app redeems the code at the token endpoint with its
   `code_verifier` within 60 seconds. Requesting the URL after that answers `400 expired_transaction`.

The proof binds the agent's key to one transaction of one client. A proof for another transaction, another client or
another URL, or a replayed `jti`, is refused. Because the agent names the client it signs for, an app cannot relay
another app's transaction to an agent and have it accepted unnoticed; an agent should only sign for the `client_id`
it was asked to sign in to.

## Revocation

The agent, its key and its owner are checked when the proof arrives, again when the code is issued (in the same
write that uses the transaction), again when the code is redeemed, and on every UserInfo call. A revoked or expired
agent, a revoked key, or an owner who was disabled, deleted or signed out everywhere since the agent was created
cannot complete a sign-in, and UserInfo refuses its tokens with `401 invalid_token`. ID tokens already issued stay
valid until they expire (5 minutes).

## Limits

Authorization: 120 requests a minute per caller address and 600 per client ID. Proofs: 30 a minute per agent and
600 per client ID. Token: 60 redemptions a minute per agent and 600 per client ID. Proof and token POSTs also charge
the caller address's shared token budget, as `/terrascale/oauth/token` does. Over a limit the answer is `429` with
`Retry-After`.

## Storage

Transactions live in `agentid_transactions` ([storage](storage.md)): the id is the SHA-256 of the authorization
request's parameters, so the same request finds the same transaction. A proof records the agent, owner and key in
the same write as the used `jti` (`agent_assertions`), asserting the key and owner versions; completing the request
sets `completed_at` in a write that asserts the transaction, key and owner versions, so it happens once and never
after a revocation that won. Rows are pruned once expired.

## Coming next

Registered clients: a TerraScale account creates a confidential client with a name and 1 to 10 https redirect URIs
(`POST /v1/terrascale/agentid-clients`, client secret shown once, with list, rotate and delete routes), which may
also request the `owner_email` scope. Owners choose per agent whether apps may see their email
(`PATCH /v1/terrascale/me/agents/{agent}` with `{"share_owner_email": bool}`, off by default).
