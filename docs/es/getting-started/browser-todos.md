---
title: Browser todos with end-user access
description: Integrate per-user TerraBase todos without sending project credentials to a browser.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Browser todos with end-user access

::: tip Bring your application session
This example uses published **`@terrascale/sdk@0.0.16`**, project-scoped end-user tokens and collection rules. Complete the [backend token integration](/es/getting-started/browser-backend-tokens) with your application's validated session. Hosted login is outside this example's scope. Never substitute a project access token in the browser.
:::

Integrate an already authenticated end-user session with the [browser example source](https://github.com/TerraScale/ts-docs/tree/main/examples/browser-todos). The browser creates, lists, edits, completes and deletes that user's todos through the SDK. It never creates collections, sets rules, exchanges project credentials, or owns identity records. Identity, organizations, projects and keys belong to ts-auth; todo data belongs to TerraBase.

## 1. Set the collection rules as the server owner

An authorized project owner must create the database and the `todos` collection **outside the browser** using the existing administrative SDK or authorized server-side HTTP. The [JavaScript quickstart](/es/getting-started/javascript) documents collection creation with an existing server client. Keep the owner's credential in the server secret store and supply it through that authorized server client. No credential values belong in this article or the browser bundle.

Set the collection rules with:

```http
PUT /v1/projects/{project}/databases/{database}/collections/todos/rules
Content-Type: application/json

{
  "owner": "/userId",
  "read": { "owner": true, "public": false, "roles": [] },
  "write": { "owner": true, "public": false, "roles": [] }
}
```

Run this operation only through an already authorized server-side HTTP client that supplies the project owner's authorization header. Use the HTTP endpoint; the SDK does not expose a rules method. Read back the same URL with `GET` and verify the complete rule object before connecting any browser. If the route is unavailable or denied, stop setup; do not broaden credentials or enable public reads/writes.

The producer must bind `/userId` to the authenticated end-user, keep ownership immutable on updates and replacements, and deny anonymous access. **A collection with no rules denies end-user access.** The browser's `/userId` query filter is a convenience; server rules enforce ownership on reads, queries, writes and changes. A browser that changes the filter or supplies another user's ID must still be denied or receive no foreign records. Collection and rule management remain unavailable to end-user tokens.

Do not store ts-auth identity records inside the todo collection or create a browser-accessible copy of project keys.

## 2. Supply your app's authenticated session

Your application owns sign-in and token renewal. Pass this integration object after sign-in succeeds:

```js
import { showAuthenticatedTodos } from './app.mjs'

// App integration supplied by your existing authentication code.
// This is not a TerraScale sign-in, session, or token-minting API.
const view = showAuthenticatedTodos({
  userId: authenticatedUserId,
  getAccessToken: () => authenticatedSession.getAccessToken(),
  project: publicProjectId,
  database: publicDatabaseName,
})

// Before logout or an account change:
view.dispose()
```

`authenticatedUserId`, `authenticatedSession`, `publicProjectId` and `publicDatabaseName` are values from **your application**, not SDK globals. Bind the user ID and token provider to the same authenticated session. The provider returns a current **end-user** access JWT as a string or a promise of a string; it must fail when that session ends. The SDK invokes it for each HTTP request and stream connection. A provider does not refresh an already open stream.

The project ID and database name are public routing identifiers. The token is sensitive: keep it in memory behind your session provider. Never paste credentials into browser controls, source, URLs, logs, local storage or session storage. Never ship a project API key, client secret, project-owner token, or a client-credentials exchange to the browser.

Connect your validated application session through the [backend token handler](/es/getting-started/browser-backend-tokens). This example supplies data access and token integration; your application supplies sign-in and session validation.

Mounting another session on the same root aborts the old requests and stream and clears the old view immediately. Call `dispose()` before logout even when no new account is mounted. A `401`, `403` or `rules_changed` error from reads, writes or the changes stream also aborts the session view and clears its records.

## 3. Run the example

Use Node.js **24.21.0 or newer** and npm **11 or newer**. From `examples/browser-todos`:

```bash
npm ci
npm run dev
```

Open the Vite URL and wire `showAuthenticatedTodos` into your application's sign-in lifecycle in `app.mjs`. The initial page intentionally offers no credential input and no fake login. The example uses Vite for browser module resolution and the SDK as its sole runtime dependency. The committed lockfile pins the published SDK and browser build tools.

The client uses the public HTTPS API and a dynamic token provider:

```js
const db = createTerraBaseClient({
  project,
  baseUrl: 'https://api.terrascale.tech',
  token: () => getAccessToken(),
}).database(database)
const todos = db.collection('todos')
```

The bearer-data routes return `Access-Control-Allow-Origin: *` and support preflight for `Authorization`, JSON writes and `If-Match`. They do not use cookies or credentialed requests: do not set `credentials: 'include'`. No origin allowlist configuration is required. CORS permits browser transport; end-user bearer validation and collection rules enforce authorization. Do not introduce a credential-bearing browser proxy to bypass authorization failures.

## 4. Understand the data path

Each todo is `{ userId, title, done }`; `create` generates its ULID document ID. Listing queries `/userId` and follows opaque continuation cursors, including empty pages. The demo caps each refresh at ten pages of fifty records, with a ten-second deadline, and reports when a larger list needs a paginated UI.

Edits and completion use `patch`; deletion uses `delete`. Both send the opaque version last rendered as `ifMatch`. A `412` leaves the mutation unreplayed and asks you to refresh before resolving the conflict. Other failed mutations can have an unknown outcome: read the list and inspect it before deciding whether another write is safe. The app and SDK never automatically retry mutations.

The UI creates DOM elements and assigns text with `textContent` or input `value`; todo text is never interpreted as HTML. Writes are serialized to prevent overlapping clicks. Owner fields are never edited.

`db.changes({ collections: ['todos'], cursor: 'now' }, { signal })` requests only todo changes. Cursor and change events schedule a fresh authorized query, so the UI does not trust stream payloads as its record cache. Refreshes are coalesced with a 250 ms delay, permit one in-flight query sequence, and keep at most one pending follow-up. Cursor-triggered refreshes close the snapshot/stream startup gap and reconcile missed changes.

Each stream runs for at most sixty seconds. A `cursor_expired`, `reset` or `reset_required` error discards the list, prevents any older pending query from restoring it, and runs a fresh authorized query. Live updates stay paused until **Refresh and reconnect** makes a new read and stream connection with the current token. Other errors or expiry also pause live updates. There is no unbounded reconnect loop. Unmount, logout, account switch and rule denial cancel requests, timers and streams. This deliberately small example does not provide offline storage, background synchronization, hosted login, or a framework-specific auth adapter.

## 5. Verify before serving users

Run the focused HTTP-fake tests:

```bash
npm test
npm run build
```

These checks exercise the browser integration and SDK request contract without starting or downloading TerraBase. The repository's `npm test` also installs this example and runs its tests. They are not evidence of deployed authorization.

Before serving users, verify your application integration with two real end-user sessions: user A creates a todo; user B cannot read, modify, delete, query or receive it through changes. Confirm immutable ownership, anonymous denial, no-rules denial, expired-token behavior, account-switch clearing, rule-revocation clearing and rejected collection/rule provisioning. Confirm the browser network trace contains end-user access tokens only and that bearer requests complete CORS preflight with the wildcard origin and no cookies. Record the SDK release and deployed producer version with that evidence. Repeat these checks for your application before releasing it to users.

</div>
