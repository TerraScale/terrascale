---
title: Authentication
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Authentication

Create an API key in the dashboard for the exact project and database your application needs, then exchange it for a short-lived access token. Your application sends that token with every request.

## API keys and access tokens

A dashboard API key has the form `<key ID>_<secret>` and is an OAuth 2.0 client credential. Exchange it at the token endpoint with the client credentials grant; the client ID is the key ID (the part before `_`) and the client secret is the whole key:

Use Bash, `curl` and `jq`, with the key already supplied privately as `TERRASCALE_API_KEY` and shell tracing off. This keeps the key out of process arguments and captures the token without printing it:

```bash
set -o pipefail
TERRASCALE_ACCESS_TOKEN="$(
  printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token'
)" && export TERRASCALE_ACCESS_TOKEN
```

The response holds `access_token` (a JWT), `token_type: Bearer`, `expires_in` (at most 60 seconds) and the granted `scope`. Send the token to `https://api.terrascale.tech` as `Authorization: Bearer <token>`. Token expiry is capped at the key's expiry. Request a new token before it expires; a revoked or expired key needs a replacement, as described under [Failed authorization](#failed-authorization). Sending the key itself as a bearer token answers `401`.

| Item | Value |
| --- | --- |
| Issuer and token endpoint | `https://auth.terrascale.tech/terrascale`, `…/oauth/token` |
| Audience | `https://api.terrascale.tech` |
| Scopes | `documents:read`, `documents:write`, `databases:read`, `databases:write` |

A key limited to one database works only for that database. **Read and write** keys carry `documents:read documents:write`.

## Key expiry and IP allowlists

**Expiry.** Choose when a key expires when you create it: 30 days, 90 days, 365 days, or never. An expired key fails the token exchange with `401` and error `invalid_client`. The key and all access tokens issued from it are refused within **2 seconds** of expiry in every region. Running requests, change streams and open Postgres sessions are cut off within **3 seconds**.

**IP allowlists.** Limit a key and its project to client networks. Each list takes up to 32 CIDR ranges, IPv4 or IPv6, such as `203.0.113.0/24` or `2001:db8::/32`. A request is allowed only when the client IP is inside the key's list and inside the project's list. Set a list to empty to allow any address. TerraBase answers a request from outside either list with `403` and this body, in every region and on the Postgres wire protocol:

```json
{"error":{"code":"ip_not_allowed"}}
```

Changes to a key's or a project's allowlist apply within 60 seconds. Keep the allowlist of a server's egress addresses current, since a changed address stops working until the list includes it.

## Revocation and rotation

Revoking an API key stops the key and every access token issued from it on all TerraBase transports in every region within **2 seconds** of the revoke being acknowledged. This covers documents, HTTP SQL, libSQL and Postgres. Running requests, change streams and open connections (including idle or busy Postgres sessions) are cut off within **3 seconds**. You do not need to wait for issued tokens to expire.

Rotating with zero grace applies the same bounds to the old secret and its tokens. With a grace period, the old secret keeps working until the grace ends; the 2-second refusal and 3-second cutoff bounds start then.

Postgres checks the key at connection startup and before every statement. Revocation or expiry closes the connection with FATAL SQLSTATE `28000` and a message starting `key_revoked:` or `key_expired:`; an open transaction rolls back. Authentication being temporarily unavailable produces FATAL `57P03` with `auth_unavailable:`. See [Postgres clients](/de/guides/postgres-clients#sqlstate-codes).

## Two-factor authentication

Protect your TerraScale account with an authenticator app (TOTP), such as 1Password, Google Authenticator or Authy.

1. Open **Profile → Security** in the dashboard and choose **Set up two-factor**, or go to `https://auth.terrascale.tech/ui/terrascale/security`. If you signed in more than 15 minutes ago, sign in again first.
2. Scan the QR code with your app, or type the key shown under it, then enter the six-digit code the app shows.
3. Save the eight recovery codes. They are shown only once, and each one signs you in once if you lose your authenticator.

Turning two-factor on or off signs you out of every other browser and the CLI. From then on, every sign-in asks for the app's code after your email code or password. To turn it off, enter a current code or an unused recovery code on the same page.

**Require two-factor for an organization.** An organization owner can turn on **Require two-factor for members** in the organization's settings, after turning on two-factor for their own account. Members without two-factor are sent to set it up and cannot open the organization in the dashboard or create API keys there until they do. Existing API keys keep working. The organization's audit log records the policy change and each member's two-factor changes and recovery code sign-ins.

## Server applications

Keep credentials in the application platform's secret store. Read them only on the server. Use a project/database scope and the minimum document permissions: reads need `documents:read`; writes need `documents:write`.

For JavaScript, pass `token` to `createTerraBaseV1Client`. For C#, pass `ApiKey` or an `AccessTokenProvider`, exclusively. The provider can supply a refreshed credential per request and must honor cancellation. Go uses `Config.Token`. Do not write a raw request wrapper or a private endpoint to bypass the SDK.

A complete [server token provider](/examples/server-token-provider/README.txt) uses published `@terrascale/sdk@0.0.13` to exchange the explicit project grant, coalesce requests and renew before expiry. Download its [module](/examples/server-token-provider/server-client.mjs) and [pinned package manifest](/examples/server-token-provider/package.json); keep the key server-side. Data operations receive a current token per request; failed authorization never replays a mutation.

## Browser applications

Keep API keys on your server; never ship one to a browser. Have your server exchange the key and call TerraBase, or hand the browser only data your server has already authorized.

The old Next.js starter's unpublished dependency and credential exchange are not the current quickstart. Do not put a secret in `NEXT_PUBLIC_*`, `VITE_*`, local storage, a shared URL or a client bundle.

## Token validation

The API checks every token's signature, issuer, audience, expiry and scopes. An SDK only checks that a token is well formed, so a token that looks valid can still answer `401` or `403`.

TerraScale uses TerraBase through the same SDK boundary as customer applications. Identity and dashboard data remain isolated from customer application databases. SDK examples must not inspect platform-owned data or offer privileged exploration flows.

## Failed authorization

TerraBase returns authorization errors as `{"error":{"code":"key_revoked"}}` (with a message where supplied). Handle the code before deciding whether to retry:

| HTTP status and code | What clients should do |
| --- | --- |
| `401 key_revoked` | Permanent for this key or retired secret and its tokens. Stop retrying; obtain a new key or use the rotated secret. |
| `401 key_expired` | Permanent for this key and its tokens. Stop retrying and obtain a new key. |
| `503 auth_unavailable` | Temporary: TerraBase cannot confirm that the key is valid and refuses access. Honor `Retry-After: 1`; retry only safe operations within a deadline. |

For other `401` responses, check token expiry and exchange again only while the key is still valid. For `403`, check the resource scope and operation permissions. A `403 ip_not_allowed` means the client address is outside a key or project allowlist; see [Key expiry and IP allowlists](#key-expiry-and-ip-allowlists). Do not retry permission failures automatically. A cutoff can leave a request's outcome uncertain: [reconcile writes before replaying them](/de/guides/errors). Never log the token, authorization header, raw server error body or a customer document while diagnosing the request.

</div>
