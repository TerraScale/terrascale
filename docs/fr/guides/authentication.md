---
title: Authentication
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Authentication

Use a credential issued through the supported access flow for the exact project and database your application needs. Transporting a token and authorizing a request are separate requirements.

## Server applications

Keep credentials in the application platform's secret store. Read them only on the server. Use a project/database scope and the minimum document permissions: reads need `documents:read`; writes need `documents:write` where those scopes are supported by the serving credential contract.

For JavaScript, pass `token` to `createTerraBaseV1Client`. For C#, pass `ApiKey` or an `AccessTokenProvider`, exclusively. The provider can supply a refreshed credential per request and must honor cancellation. Go uses `Config.Token`. Do not write a raw request wrapper or a private endpoint to bypass the SDK.

## Browser applications

Keep privileged application credentials on your server. A browser needs its own supported short-lived, scoped authorization flow. Register callbacks exactly, use PKCE where supported, and renew access before expiry. A working Node example is not proof that browser sign-in, credential exchange, CORS and live updates work on the deployed service.

The old Next.js starter's unpublished dependency and credential exchange are not the current quickstart. Do not put a secret in `NEXT_PUBLIC_*`, `VITE_*`, local storage, a shared URL or a client bundle.

## Service availability and migration

Project access JWTs are the intended identity integration. Current candidate compatibility evidence also includes historical API keys. A client's token syntax validation does not verify server-side JWT claims, signatures, audience, expiry or authorization. Use only the credential type supported by your provisioned serving API; do not invent an issuance endpoint.

TerraScale uses TerraBase through the same SDK boundary as customer applications. Identity and dashboard data remain isolated from customer application databases. SDK examples must not inspect platform-owned data or offer privileged exploration flows.

## Failed authorization

Handle `401` by checking expiry, revocation and the supported issuance flow. Handle `403` by checking the resource scope and operation permissions. Do not retry these errors automatically. Never log the token, authorization header, raw server error body or a customer document while diagnosing the request.

</div>
