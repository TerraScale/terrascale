---
title: Agent identities
---

# Agent identities

An agent identity gives an AI agent its own principal and key, with a person accountable for it. The agent generates an ES256 signing key pair and keeps the private key. TerraScale stores only the public key. The owner approves the agent and can revoke it.

Use project agents for the people who use your application. Use TerraScale account agents for a coding agent that calls TerraScale MCP or signs in to another app.

| Use | Identity | Token destination |
| --- | --- | --- |
| Your app's users delegate work | Agent owned by a project user | Your backend |
| A coding agent uses TerraScale | Agent owned by your TerraScale account | TerraScale MCP |
| An agent signs in to any app | TerraScale account agent through the agents OIDC issuer | That app's sign-in client |

An agent acts for its owner by default. Its token identifies both the owner and the acting agent. A project administrator can also allow an agent to act as itself. Either way, tokens last at most five minutes and never include a refresh token.

## Agents for your app's users

Start with [end-user authentication](/guides/end-user-authentication) so the owner has an account in your project. Enrollment and token issuance use that project's ID, not the TerraScale account issuer.

### Set the agent policy

Open **Authentication › Agents** in the dashboard. An administrator sets the scopes new agents may request, one per line. A viewer can inspect the policy and agent list.

The equivalent management request is:

```http
PUT https://auth.terrascale.tech/v1/{project}/agent-policy
Content-Type: application/json

{"allowed_scopes":["invoices:read","invoices:write"]}
```

Use an administrator's TerraScale sign-in token or an unrestricted project secret key with `auth:write`. Reading the policy at the same route needs viewer access or `auth:read`.

The policy accepts 0–64 unique scopes. No policy, or an empty list, prevents new agents. Changing the policy does not change scopes on existing agents. Your backend decides what each application scope permits.

### Enroll and approve

The JavaScript examples use the current SDK source. Follow [JavaScript source installation](/getting-started/javascript#install-the-sdk); do not assume the previous published package has these helpers. Set `project` to your project ID.

```js
import { createTsAuthClient } from '@terrascale/sdk/ts-auth';

const auth = createTsAuthClient({
  apiBaseUrl: 'https://auth.terrascale.tech',
  instanceSlug: project,
});
const enrollment = await auth.agents.enroll({
  project,
  name: 'Invoice agent',
  scopes: ['invoices:read'],
});
if (!enrollment.ok) throw new Error(enrollment.error.code);
console.log(
  enrollment.value.userCode,
  enrollment.value.verificationUriComplete,
  enrollment.value.fingerprint,
);
const identity = await enrollment.value.wait();
if (!identity.ok) throw new Error(identity.error.code);
```

The owner opens the verification URL and signs in to your project's hosted page. They check the displayed account, agent name, scopes, key fingerprint and code against the agent's own display. Opening the URL does not approve anything.

**Approve** requires fresh verification, including an authenticator code when MFA is on. **Deny** ends the request. The enrollment code lasts ten minutes. The SDK waits at the server's polling interval and collects the approved identity once.

The returned `identity.value` contains `agentId`, `kid` and an opaque in-memory signer. Its WebCrypto private key is non-extractable. Keep the signer in the agent process; only its public JWK leaves that process.

The SDK supports Node.js 22 and 24, and browsers with WebCrypto where requests from the browser origin are permitted.

For a terminal agent, the current [CLI source](https://github.com/TerraScale/terrascale/tree/main/sdk/go) provides:

```sh
ts agent enroll --project "$PROJECT" --name "Invoice agent" --scope invoices:read
```

The owner completes the same browser approval. Keep the CLI's stored private key private. Use the agent ID returned by enrollment for later token requests:

```sh
ts agent token --project "$PROJECT" --agent "$AGENT_ID" --scope invoices:read
```

The token command prints a bearer token for piping to a trusted consumer. Do not paste its output into chat or logs.

### Get a short-lived token

Continue in the same JavaScript process:

```js
const token = await auth.agents.getToken({
  project,
  ...identity.value,
  scope: 'invoices:read',
});
if (!token.ok) throw new Error(token.error.code);
```

Send `token.value.access_token` to your backend as `Authorization: Bearer <token>`. The SDK caches tokens in memory and obtains another when the cached token is within thirty seconds of expiry.

Without `scope`, the token receives all of the agent's allowed scopes. A supplied scope must be a subset. Tokens never outlive the agent's expiry.

For an HTTP implementation, discover the project token endpoint and send a JWT bearer grant:

```http
POST https://auth.terrascale.tech/{project}/oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&assertion=<signed-assertion>&scope=invoices:read&requested_subject=owner
```

Sign the assertion with ES256 and the registered key's `kid`. Set `iss` and `sub` to the agent ID, `aud` to the exact token endpoint URL, a unique `jti`, and integer Unix-second `iat` and `exp` no more than 300 seconds apart. Never send the private key or embed a replacement public key in the assertion header.

The response contains `access_token`, `token_type: Bearer`, `expires_in` and `scope`. It contains no ID token or refresh token.

### Verify in your backend

Use the project issuer's keys and agent-specific verification. A decoded token alone does not prove an identity.

```js
import { verifyAgentToken } from '@terrascale/sdk/ts-auth';

const verified = await verifyAgentToken(accessToken, { project });
if (!verified.ok) throw new Error(verified.error.code);
const actor = verified.value;
if (actor.actingFor !== 'owner') throw new Error('Delegation required');
if (!actor.scopes.includes('invoices:read')) throw new Error('Scope required');
```

After verification, authorize the requested invoice against `actor.ownerId` using your application's ownership rules. Record `actor.agentId` as the actor for accountability. A scope check alone does not establish access to a particular invoice.

`verifyAgentToken` checks the ES256 signature, project issuer, time validity, `token_use=agent`, `actor_type=agent`, and the relationship between `sub`, `agent_id`, `act` and `owner_sub`.

| Claim | Delegated token, the default | Agent acting as itself |
| --- | --- | --- |
| `sub` | Owner's project user ID | Agent ID |
| `act` | `{"sub":"<agent ID>"}` | Absent |
| `owner_sub` | Absent | Owner's project user ID |
| `agent_id` | Agent ID | Agent ID |
| `token_use`, `actor_type` | `agent`, `agent` | `agent`, `agent` |
| `scope` | Granted application scopes | Granted application scopes |

These access tokens have no `aud`. Do not pass a data API audience to the helper: that would require an audience these tokens do not have. They do not authorize TerraScale's data API, userinfo or platform management endpoints.

To allow self-subject tokens, enroll with `allowSelfSubject: true` and request `subject: 'agent'` in `agents.getToken`. The CLI equivalents are `--allow-self-subject` at enrollment and `--subject agent` at token issuance. Your backend must explicitly authorize that agent principal rather than treating it as the owner.

### Inspect and revoke

**Authentication › Agents** lists project agents. Filter by owner, inspect scopes, status and keys, then revoke a key or the whole agent. The dashboard does not create agents; use enrollment or the management API.

| Management operation | Route |
| --- | --- |
| List agents | `GET /v1/{project}/agents?owner={user}&limit=50` |
| Read an agent and its keys | `GET /v1/{project}/agents/{agent}` |
| Revoke an agent | `DELETE /v1/{project}/agents/{agent}` |
| Revoke a key | `DELETE /v1/{project}/agents/{agent}/keys/{kid}` |

Reads need viewer access or an unrestricted project key with `auth:read`; writes need an administrator or `auth:write`. An end user can list or revoke their own agents through `GET /v1/{project}/me/agents` and `DELETE /v1/{project}/me/agents/{agent}` using the project's hosted session cookie. Mutations require the auth public origin as `Origin`.

Revocation blocks new tokens. Offline verification still accepts an already issued token until its expiry, at most five minutes. See [Limits and revocation](#limits-and-revocation).

## Agents for your TerraScale account

A coding agent can act for your TerraScale account through [TerraScale MCP](/guides/mcp). Account agents are created by enrollment, with your approval. The allowed scopes are fixed: `mcp:read` and `mcp:write`.

The project-oriented JavaScript and CLI examples above take a project ID. For an account agent, use the platform HTTP enrollment flow:

```http
POST https://auth.terrascale.tech/v1/terrascale/agent-enrollments
Content-Type: application/json

{"name":"Coding agent","requested_scopes":["mcp:read","mcp:write"],"public_jwk":<public JWK>}
```

Replace `<public JWK>` with the public JWK object exported from your ES256 signing key. Keep its private key with the agent; never include a private `d` member.

1. Show the returned `user_code`, key fingerprint and `verification_uri_complete` to the owner. Approval uses the owner's TerraScale account and fresh verification.
2. Collect the identity at `POST /v1/terrascale/agent-enrollments/{enrollment_id}/token`, no faster than the returned `interval`. Send `{"proof":"<ES256 JWT>"}` signed with the enrollment key, with the exact polling URL as `aud`, a unique `jti` and integer `iat` and `exp` at most 300 seconds apart. Each accepted poll needs a later `iat` than the previous poll.
3. Use the returned `agent_id` and `kid` to sign a JWT bearer assertion for `https://auth.terrascale.tech/terrascale/oauth/token`, following the assertion rules above. Request `mcp:read` or `mcp:write` and keep `requested_subject=owner`.
4. Send the access token to `https://mcp.terrascale.tech/` as `Authorization: Bearer <agent token>`. Renew it with the agent's key before expiry.

The MCP server acts within the owner's permissions. `mcp:read` permits read tools; `mcp:write` permits read and write tools. Neither permits destructive tools or billing, including billing reads.

An agent cannot delete resources, change members or roles, create or rotate credentials, change sign-in settings or security policies, deploy database rules, deploy or invoke functions, set function secrets, create file buckets or change their rules, or call `execute_sql`. Organizations requiring two-factor authentication remain unavailable to agents; agent tokens carry no two-factor state or platform role.

The server checks the agent through an owner-token exchange on every MCP request. Revoking it, disabling its owner or signing the owner out everywhere refuses the next request. A project agent token or self-subject token cannot use this MCP flow.

Open **Profile › Agents** in the dashboard to list and revoke your account's agents. This list is separate from **Authentication › Agents** in a project.

## Sign in as agent for any app

Any app can use TerraScale account agents as a custom OpenID Connect provider. This issuer is separate from both your project's issuer and the TerraScale account issuer.

| Setting | Value |
| --- | --- |
| Issuer | `https://auth.terrascale.tech/agents` |
| Discovery URL | `https://auth.terrascale.tech/agents/.well-known/openid-configuration` |
| Response type | `code` |
| PKCE | `S256`, required |
| Scopes | `openid` required; `profile` optional; `owner_email` for registered clients |
| Token signing | ES256 |
| Token lifetime | Five minutes |

Only authorization code with PKCE is supported. There is no implicit, hybrid or refresh grant. The access token works only at this issuer's UserInfo endpoint, not at other TerraScale APIs.

### Open and registered clients

An open client needs no registration or secret. Use your app's canonical HTTPS origin as `client_id`, such as `https://app.example.com`: lowercase ASCII host, no userinfo, path, trailing slash or default port. Use punycode for an international hostname.

Its `redirect_uri` must be an HTTPS URL on exactly that origin. Another subdomain or port is not accepted. Use token endpoint authentication `none`.

Register a confidential client when your integration requires a secret or needs the owner's email. Use your TerraScale account's platform sign-in access token and a fresh `create_oauth_client` [step-up proof](/guides/sensitive-actions):

```http
POST https://auth.terrascale.tech/v1/terrascale/agentid-clients
Authorization: Bearer <platform sign-in access token>
X-TerraScale-Step-Up-Proof: <create_oauth_client proof>
Content-Type: application/json

{"name":"Example app","redirect_uris":["https://app.example.com/auth/callback"]}
```

The `201` response includes `client_id`, `name`, `redirect_uris`, `created_at` and a `client_secret` shown once. Save the secret on your server. The registered client ID is a ULID, not the app's origin.

The name is 1–64 characters and appears as `client_name` to the agent and waiting page. Supply 1–10 distinct absolute HTTPS redirect URLs in canonical form: lowercase scheme and host, no default port, userinfo or fragment. Every authorization request's `redirect_uri` must exactly equal a registered URL. Each account can register at most ten clients.

| Client operation | Route | Step-up proof |
| --- | --- | --- |
| Register | `POST /v1/terrascale/agentid-clients` | `create_oauth_client` |
| List, without secrets | `GET /v1/terrascale/agentid-clients` | None |
| Rotate secret | `POST /v1/terrascale/agentid-clients/{client_id}/secret/rotate` | `rotate_oauth_client` |
| Delete | `DELETE /v1/terrascale/agentid-clients/{client_id}` | `delete_oauth_client` |

Each management request uses the owning account's platform sign-in access token. Send the proof in `X-TerraScale-Step-Up-Proof`. An agent-acted token is refused with `403 owner_required`; another account's client is `404`.

Rotation accepts an optional `{"grace_seconds":3600}` body, with a range of 0–86400 seconds and a default of 3600. It returns the new secret once and `previous_secret_expires_at`. The old secret works until that deadline. Rotating again during grace replaces the previous secret; zero grace ends its use immediately.

At code redemption, authenticate a registered client with `client_secret_basic` or `client_secret_post`. PKCE (`S256`) remains required. Request `openid profile`, adding `owner_email` only when needed. An open client requesting `owner_email` receives `invalid_scope` at its redirect URI.

### Owner email consent

In **Profile › Agents**, the agent's owner chooses **Share my email with apps**. Sharing is off by default for every agent. The equivalent request uses the owner's platform sign-in access token:

```http
PATCH https://auth.terrascale.tech/v1/terrascale/me/agents/{agent}
Authorization: Bearer <platform sign-in access token>
Content-Type: application/json

{"share_owner_email":true}
```

The response is the agent, including `share_owner_email`; the owner's agent list includes this field too. An agent-acted token cannot change consent (`403 owner_required`).

Consent covers every registered client the agent signs in to, including clients registered by other accounts. It is not a separate consent choice for each app. The agent still chooses which client's sign-in it proves.

A registered client granted `owner_email` receives both `owner_email` and `owner_email_verified` only while sharing is on. Otherwise both claims are omitted without an error. ID tokens capture consent and email at code redemption. UserInfo reads current consent on every call with `Cache-Control: no-store`: turning sharing off removes the email there immediately. Previously issued ID tokens retain it until their five-minute expiry.

### Claims

| ID token claim | When present | Meaning |
| --- | --- | --- |
| `iss`, `aud` | Always | Agents issuer and your client ID |
| `iat`, `exp`, `jti` | Always | Issue time, expiry and token ID |
| `sub` | Always | Agent subject, pairwise for your client ID |
| `actor_type` | Always | `agent` |
| `nonce` | When sent | Your authorization attempt's nonce |
| `name` | With `profile` | Agent name chosen by its owner |
| `owner_sub` | With `profile` | Accountable owner, pairwise for your client ID |
| `owner_email`, `owner_email_verified` | `owner_email` scope, registered client and owner consent | Owner's TerraScale email and whether it is verified |

The same agent and client ID get the same `sub`. Different client IDs get unrelated subjects, including for `owner_sub`. The provider does not reveal TerraScale account or agent IDs. The profile `name` is shared across clients, so it can still help apps correlate an agent. Shared `owner_email` is also the same across registered clients and can correlate the owner's account despite pairwise subjects.

Ungranted claims are omitted. UserInfo returns the granted identity claims and checks that the agent, proof key and owner remain usable. Do not use the owner subject as the agent's subject, or assume the two principals have the same permissions in your app.

### Generic OIDC client

Configure a maintained OIDC library with:

```json
{
  "issuer": "https://auth.terrascale.tech/agents",
  "client_id": "https://app.example.com",
  "redirect_uri": "https://app.example.com/callback/agent",
  "response_type": "code",
  "scope": "openid profile",
  "token_endpoint_auth_method": "none",
  "code_challenge_method": "S256"
}
```

For a registered client, replace the origin client ID with the returned ULID and use a registered redirect URL. Configure `client_secret_basic` or `client_secret_post` and the server-held secret. Use `openid profile owner_email` if you need consented owner email.

Let the library generate and bind a one-time `state`, `nonce` and PKCE verifier to the initiating session. Validate callback state before redeeming the code. Before creating a session, verify the ID token's ES256 signature against the discovered JWKS, exact issuer, client audience, expiry and nonce. When using UserInfo, require its `sub` to match the validated ID token.

### Auth.js

Add this OIDC provider to your Auth.js providers:

```js
{
  id: 'agent',
  name: 'Sign in as agent',
  type: 'oidc',
  issuer: 'https://auth.terrascale.tech/agents',
  clientId: 'https://app.example.com',
  client: { token_endpoint_auth_method: 'none' },
  checks: ['pkce', 'state', 'nonce'],
  authorization: { params: { scope: 'openid profile' } },
}
```

Serve the callback on that exact origin. Keep the agent subject and owner subject separate in your session model.

For a registered client, use its returned ID and secret instead:

```js
{
  id: 'agent',
  name: 'Sign in as agent',
  type: 'oidc',
  issuer: 'https://auth.terrascale.tech/agents',
  clientId: process.env.AGENT_CLIENT_ID,
  clientSecret: process.env.AGENT_CLIENT_SECRET,
  client: { token_endpoint_auth_method: 'client_secret_basic' },
  checks: ['pkce', 'state', 'nonce'],
  authorization: { params: { scope: 'openid profile owner_email' } },
}
```

Register Auth.js's exact callback URL when creating the client. The email claims remain optional because the owner controls sharing.

### Clerk

Use a custom OIDC provider. For a connection that requires a secret, register the connection's exact callback URL and use:

```text
Discovery: https://auth.terrascale.tech/agents/.well-known/openid-configuration
Client ID: <registered client_id>
Client secret: <server-held client_secret>
Scopes: openid profile
PKCE: S256
Token endpoint authentication: client_secret_basic or client_secret_post
```

For a configuration supporting public clients, use the open-client origin ID and authentication `none` instead. Add `owner_email` only for a registered client and handle absent email claims.

### Supabase

For a third-party OIDC integration, complete the authorization-code exchange in your app with:

```text
Issuer: https://auth.terrascale.tech/agents
Client ID: https://app.example.com
Redirect URI: https://app.example.com/callback/agent
Scopes: openid profile
PKCE: S256
Token endpoint authentication: none
```

Pass the validated ID token through `signInWithIdToken` only with the matching provider configured in Supabase. Do not treat this issuer as a built-in social provider. Your app must implement the code exchange and callback validation; adding an issuer URL alone does not complete it.

If your integration requires a secret or owner email, use a registered client, its exact redirect URL and server-side `client_secret_basic` or `client_secret_post` authentication. Keep PKCE and add `owner_email` only when needed.

### Auth0

Use an enterprise OpenID Connect connection on the front channel, with:

```text
Discovery: https://auth.terrascale.tech/agents/.well-known/openid-configuration
Client ID: <registered client_id>
Client secret: <server-held client_secret>
Scopes: openid profile
PKCE: S256
Response type: code
Token endpoint authentication: client_secret_basic or client_secret_post
```

Register the connection's exact callback URL. Add `owner_email` if needed and handle absent claims when the owner does not share their email. A public-client connection can instead use an open origin ID, no secret and authentication `none`.

### How the agent completes sign-in

1. Your app starts an authorization request. The agent opens that URL with `Accept: application/json`. The response includes `status: pending`, `transaction_id`, `proof_url`, `client_id`, `client_name` and `expires_at`. A `Link` header with `rel="agent-proof"` also identifies the proof URL. The transaction lasts five minutes.
2. The agent checks the client ID and transaction against the sign-in it initiated. It must not sign a transaction relayed by an unrelated app. It posts `{"proof":"<ES256 JWT>"}` to the returned proof URL, signed with an active account-agent key.
3. The proof header has `alg: ES256` and the registered `kid`. Its claims bind `iss` and `sub` to the agent ID, `aud` to the exact proof URL, `txn` to the transaction, and `client_id` to the intended app. It also carries a one-time `jti` and integer `iat` and `exp` at most 300 seconds apart. A successful proof returns `204`.
4. The agent requests the same authorization URL again. It redirects once to your callback with `code`, `state` and `iss`. Your app redeems the code with its original PKCE verifier within sixty seconds.

A browser receives a waiting page that redirects after the proof succeeds. Opening the page alone does not prove the agent's identity. Account enrollment and owner approval happen before this sign-in flow.

## Limits and revocation

| Limit | Value |
| --- | --- |
| Live agents per owner | 25 |
| Agent creations per owner | 100 per hour |
| Active keys per agent | 2 |
| Registered sign-in clients per account | 10 (`409 client_limit`) |
| Registered client name / redirect URLs | 1–64 characters / 1–10 distinct canonical HTTPS URLs |
| Secret rotation grace | 0–86400 seconds; default 3600 |
| Agent name | 1–64 characters |
| Scopes on an agent | 1–64 unique scopes, each allowed by policy |
| Enrollment starts | 10 per IP per 15 minutes; 300 per project per hour, or for the whole account tenant |
| Enrollment code | 600 seconds; poll no faster than the returned interval |
| Agent token issuance | 60 per agent per minute; 600 across an owner's agents per minute |
| Agent access token | At most 300 seconds; capped by agent expiry |
| OIDC transaction / authorization code | Five minutes / sixty seconds |
| OIDC authorization requests | 120 per caller address per minute; 600 per client ID per minute |
| OIDC proofs | 30 per agent per minute; 600 per client ID per minute |
| OIDC token redemptions | 60 per agent per minute; 600 per client ID per minute |

Token and proof requests also consume their endpoint's caller-address budget. Rate limits return `429` with `Retry-After`.

To rotate a project agent's key, add a new public key, switch the agent to its returned key ID, then revoke the old key. The two-key limit lets you make this transition without sharing a private key. A revoked key cannot be registered again while its key record remains.

Revoking an agent revokes its active keys. Signing the owner out everywhere, resetting their password, changing their email or disabling their account makes existing agents unusable. Enabling the owner again does not restore those agents; enroll again. Deleting the owner removes their agents.

For your backend, an issued agent token remains valid offline until expiry. MCP rechecks through the owner exchange on each request. OIDC checks the agent, owner and proof key before accepting proof, issuing a code, redeeming it and serving UserInfo; an already issued ID token remains valid until its five-minute expiry. Set your app's own session and reauthentication policy accordingly.

Deleting a registered client refuses its next authorization, code redemption and secret use; UserInfo rejects its access tokens with `401 invalid_token`. Disabling or deleting the account that registered it also stops the client. Rotation without grace or deletion that wins a race with redemption makes that redemption fail without consuming the code. Client creation, rotation and deletion require the owner's live sign-in state; signing out everywhere first makes these changes fail with `409`.
