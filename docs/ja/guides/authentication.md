---
title: Authentication
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Authentication

Create an API key in the dashboard for the exact project and database your application needs, then exchange it for a short-lived access token. Transporting a token and authorizing a request are separate requirements.

## API keys and access tokens

A dashboard API key has the form `<key ID>_<secret>` and is an OAuth 2.0 client credential. Exchange it at the token endpoint with the client credentials grant; the client ID is the key ID (the part before `_`) and the client secret is the whole key:

```bash
curl -sS --fail-with-body -u "${TERRASCALE_API_KEY%%_*}:$TERRASCALE_API_KEY" \
  -d grant_type=client_credentials https://auth.terrascale.tech/terrascale/oauth/token
```

The response holds `access_token` (a JWT), `token_type: Bearer`, `expires_in: 900` and the granted `scope`. Send the token to `https://api.terrascale.tech` as `Authorization: Bearer <token>`. The token is valid for 15 minutes; request a new one before it expires or after a `401`. Sending the key itself as a bearer token answers `401`.

| Item | Value |
| --- | --- |
| Issuer and token endpoint | `https://auth.terrascale.tech/terrascale`, `…/oauth/token` |
| Audience | `https://api.terrascale.tech` |
| Scopes | `documents:read`, `documents:write`, `databases:read`, `databases:write` |

A key limited to one database works only for that database. **Read and write** keys carry `documents:read documents:write`.

## Server applications

Keep credentials in the application platform's secret store. Read them only on the server. Use a project/database scope and the minimum document permissions: reads need `documents:read`; writes need `documents:write` where those scopes are supported by the serving credential contract.

For JavaScript, pass `token` to `createTerraBaseV1Client`. For C#, pass `ApiKey` or an `AccessTokenProvider`, exclusively. The provider can supply a refreshed credential per request and must honor cancellation. Go uses `Config.Token`. Do not write a raw request wrapper or a private endpoint to bypass the SDK.

## Browser applications

Keep privileged application credentials on your server. A browser needs its own supported short-lived, scoped authorization flow. Register callbacks exactly, use PKCE where supported, and renew access before expiry. A working Node example is not proof that browser sign-in, credential exchange, CORS and live updates work on the deployed service.

The old Next.js starter's unpublished dependency and credential exchange are not the current quickstart. Do not put a secret in `NEXT_PUBLIC_*`, `VITE_*`, local storage, a shared URL or a client bundle.

## Service availability and migration

Project access JWTs are the intended identity integration. Current candidate compatibility evidence also includes historical API keys. A client's token syntax validation does not verify server-side JWT claims, signatures, audience, expiry or authorization. Exchange API keys only at the token endpoint above.

TerraScale uses TerraBase through the same SDK boundary as customer applications. Identity and dashboard data remain isolated from customer application databases. SDK examples must not inspect platform-owned data or offer privileged exploration flows.

## Failed authorization

Handle `401` by checking expiry and revocation, then run the token exchange again. Handle `403` by checking the resource scope and operation permissions. Do not retry these errors automatically. Never log the token, authorization header, raw server error body or a customer document while diagnosing the request.

</div>
