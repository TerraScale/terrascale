---
title: Browser tokens from your app backend
description: Connect an existing app session to project-scoped end-user tokens without exposing project keys.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Browser tokens from your app backend

::: tip Connect your existing backend
The browser example uses published **`@terrascale/sdk@0.0.16`** with the end-user token endpoint and collection rules. Supply your application's validated session, then run the isolation checks below with your own test users before serving customers. An unavailable mint route is a setup failure; never substitute a project token in the browser.
:::

Connect your **existing validated application session** to the [browser todos example](/fr/getting-started/browser-todos). Your backend mints a short-lived end-user JWT, your browser obtains it through a same-origin app route, and the SDK sends it directly to `https://api.terrascale.tech`. Your app continues to own sign-in, session validation, logout and account switching.

The [backend handler and browser provider source](https://github.com/TerraScale/ts-docs/tree/main/examples/browser-backend-tokens) use standard platform APIs with no new dependencies. They require a server runtime supporting `Request`, `Response`, `fetch` and `AbortSignal` (Node.js **24.21.0+** provides these). There is no credential input, provisioning flow or replacement login implementation.

## 1. Verify owner rules and the server credential

Complete the existing [server-owner collection and rule setup](/fr/getting-started/browser-todos#_1-set-the-collection-rules-as-the-server-owner) first. Read the rules back before connecting a browser. The `todos` collection binds its immutable `/userId` owner to the signed end-user identity and denies other users. **Collections without rules deny end-user access by default.** A browser query filter is not an authorization boundary.

Use an existing authorized **project key without a database restriction**, carrying **both `documents:read` and `documents:write`**. It must belong to the requested live project and organization. A database-restricted key from the general server quickstart is not eligible for this mint route. Keep the **whole** `<key ID>_<secret>` credential in your backend platform's secret store. Supply it only to the server module; never import that module or its configuration into a browser build.

The token is scoped to the project. Collection rules determine which documents its signed user identity and roles can access; the mint credential's write permission does not make every end user an owner. End-user tokens cannot provision collections or rules. This example fixes the project and database in your app integration; it accepts neither from token-request inputs.

## 2. Mount the token handler in your existing backend

Copy `token-handler.mjs` and `read-token-json.mjs` into your backend and initialize the handler once:

```js
import { createTokenHandler } from './token-handler.mjs'

const handleToken = createTokenHandler({
  origin: 'https://app.example', // Your exact public HTTPS app origin, no trailing slash.
  project: publicProjectId,
  projectKey: serverProjectKey,
  readSession: readValidatedAppSession,
})

// Inside your existing Request -> Response app router:
if (new URL(request.url).pathname === '/api/terrabase-token') {
  return handleToken(request)
}
```

`publicProjectId`, `serverProjectKey`, `readValidatedAppSession` and `request` are integration values supplied by **your application**. The key comes from your existing server secret-store integration. This is a route inside the same app that already validates your session, not a separately hosted token proxy. Your HTTP framework must adapt its request/response types to the standard `Request`/`Response` API and propagate client disconnection through `request.signal`.

Implement `readValidatedAppSession(request, { signal })` using your app's existing session validator. It returns `null` for a missing, invalid, revoked or expired session, or `{ userId, roles? }` from trusted server-side account data. It must honor cancellation. The user ID must be a stable canonical uppercase ULID; if your app uses different IDs, maintain the mapping in trusted server data. Roles, when present, are an array of strings assigned by your app's authorization policy. Do not copy a user ID, roles, claims, project or key from request JSON, query parameters, unverified cookies or browser-supplied headers. Validate any application session cookie before trusting its contents.

The app route requires a bodyless `POST` with an `Origin` exactly equal to the configured HTTPS origin. Missing, `null`, cross-origin and prefix-matching origins fail. The expected origin comes from trusted server configuration, never `Host` or forwarded request headers. This exact Origin check supplies CSRF protection for your app's existing cookie session. Keep the app session's existing `Secure`, `HttpOnly` and `SameSite` cookie protections. The route emits no CORS permission and rejects preflight.

The backend mints the end-user token with:

```http
POST https://auth.terrascale.tech/v1/terrascale/projects/{project}/end-user-tokens
Authorization: Bearer <whole server-only project key>
Content-Type: application/json

{
  "user_id": "01ARZ3NDEKTSV4RRFFQ69G5FAW",
  "claims": { "roles": ["reader"] }
}
```

The ULID and role above are illustrative, not credentials or role assignments to copy. `claims` is optional; only `claims.roles` is supported. Unknown properties are rejected, and arbitrary custom claims are not supported. This server custom-token mint does not use the OAuth client-credentials exchange documented for [server access](/fr/guides/authentication#server-applications). The mint endpoint uses no cookies and offers no browser CORS flow.

The producer returns `{ access_token, token_type: "Bearer", expires_in }`. The reported lifetime is at most **900 seconds**, further bounded by the project key's remaining lifetime. TerraBase enforces `exp` strictly (its 30-second clock-skew allowance applies only to `nbf`), so an issued JWT stays usable for at most **15 minutes** after issuance. Revoking the key prevents **future mints**. Logging out of your app or changing roles also does not erase an already issued bearer token. Plan authorization changes around that acceptance window and the collection-rule contract.

The **complete signed compact JWT** is limited to **8192 ASCII bytes**. The producer checks the final encoded token after signing and returns `400 invalid_request` if it exceeds that cap, including when trusted role assignments make it too large. Both ends of this example accept a token exactly at the cap and reject larger tokens. This byte cap is separate from the JSON response-body bound below.

The app handler allowlists the token fields and adds `user_id` from the validated session so the browser can detect an account change. Both backend and browser use a shared streaming JSON reader with a fixed **16,384-byte** response limit, including for chunked bodies without `Content-Length`; overflow cancels the reader before parsing. The handler sets `Cache-Control: no-store` on success and failure, passes a five-second cancellation deadline to the session validator and mint request, refuses redirects, and never forwards app cookies to the producer. Upstream rejection, malformed responses and network failures become generic `502`/`503` responses; no upstream error body or credential is returned or logged. It sends once without retries. A producer `404` means the project or route is unavailable; widening the key is not a fix.

## 3. Bind the browser provider to the current app session

Copy `browser-session.mjs`, `connect-todos.mjs` and `read-token-json.mjs` into your browser app. For the existing example, place this integration next to `examples/browser-todos/app.mjs` and keep those modules in the sibling example directory:

```js
import { showAuthenticatedTodos } from './app.mjs'
import { connectTodos } from '../browser-backend-tokens/connect-todos.mjs'

let active

// Call from your existing successful sign-in/session-change callback.
export function onAppSession({ userId, project, database }) {
  onAppLogout()
  active = connectTodos(showAuthenticatedTodos, { userId, project, database })
}

// Call before logout or account switching, even if no new session is ready.
export function onAppLogout() {
  active?.dispose()
  active = undefined
}
```

The lifecycle callbacks are application integration points, not TerraScale sign-in APIs. Obtain `userId` from the app's validated session bootstrap; `project` and `database` are public routing identifiers. Serve the browser app and `/api/terrabase-token` on the **same HTTPS origin**, including in your integration environment. A separate Vite origin will fail CSRF/session checks; connect it through your existing same-origin development setup rather than relaxing the origin check.

`createBrowserSession` supplies the exact `{ userId, getAccessToken, project, database }` interface expected by `showAuthenticatedTodos`, plus `dispose()`. For each SDK token-provider call, it makes a bodyless same-origin POST using only your app's existing session cookie. It sends no user ID, roles or project key. The provider keeps no token cache and uses a five-second deadline, so each data request or new stream obtains a token through fresh app-session validation. This adds a mint request per SDK request; larger applications can add session-bound, in-memory coalescing and expiry-aware caching after measuring that load.

The JWT is a **signed data-access identity**, not an application login/session. It must never become your app's session cookie or authorization source for app administration. TerraBase validates its signature, issuer, audience, expiry, project and rules; the SDK's token-shape check does not establish those properties. The browser must receive only the end-user JWT, never a project access token or the project key. Keep bearer tokens in memory and out of URLs, browser storage, source, logs and error reports.

`connectTodos` binds the provider's required `onInvalidated` callback to **both** that provider and its exact todo view. On an app `401`/`403` or mismatched `user_id`, the provider invokes this callback once, before rejecting: private records clear, pending reads/writes cancel and the stream closes. This cleanup must be explicit because SDK 0.0.16 sanitizes provider errors and does **not** preserve their `status`; the todo view cannot detect an app-session denial from the rejected provider alone. The helper also disposes a view if invalidation happened before the view was assigned. Explicit disposal never invokes the callback, and disposed providers ignore late denials, so an old A request cannot clear B's new view.

Always call the returned `dispose()` when the app session ends or changes, as the lifecycle callbacks above do. The provider aborts pending token fetches and rejects late results. An already open stream does not call the provider again; the example bounds streams to sixty seconds and requires an explicit refresh/reconnect after they stop. Propagate your app's session-expiry and account-change events to these lifecycle callbacks. Direct TerraBase `401`, `403` and rule-denial responses also trigger the todo view's existing cleanup.

## 4. Run fake tests, then verify rollout separately

From the docs repository root:

```bash
npm ci --prefix examples/browser-todos --ignore-scripts
node --test examples/browser-backend-tokens/*.test.mjs
```

The root `npm test` also runs this suite after installing the browser example's pinned SDK. The tests fake HTTP requests/responses; they do not run or download TerraBase, call live services, establish real sessions or verify JWT signatures. They check session-derived claims, CSRF rejection, safe failures, bounded streaming bodies, cancellation and token lifetimes. Integrated tests use the real SDK and `mountTodos` to verify app-session denial clears private rows and cancels streams/reads, initial invalidation is safe, and late A failures cannot clear B's view.

Before serving customers, record the deployed producer version and SDK version with these live checks:

1. Mint for a validated user A session; confirm the browser network trace contains only an end-user JWT and the project key stays on the backend. Verify the producer's project/org/key checks and bounded expiry.
2. Repeat the [two-user owner-rule checks](/fr/getting-started/browser-todos#_5-verify-before-serving-users): B cannot read, query, write, delete or receive A's records through changes. Confirm default denial with no rules and no end-user collection/rule provisioning.
3. Verify logout, expiry and account switching clear the UI and cancel old requests. Verify foreign/missing Origin requests and missing/invalid app sessions cannot mint.
4. Verify key revocation blocks new mints while documenting the expiry window for previously issued JWTs. Confirm bearer-data CORS works without cookies and the mint route remains server-only.

These deployment and authorization checks remain **pending**. Passing fake tests alone does not authorize customer rollout.

</div>
