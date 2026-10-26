# Browser todos

SDK 0.0.16 is published and pinned in the lockfile. Connect your application’s validated session through the backend token guide, then verify collection rules and user isolation before serving users. No hosted login or credential-entry form is included.

Read [the browser todo guide](../../docs/getting-started/browser-todos.md) for server-owner rules, the end-user session contract and live verification requirements.

1. Run `npm ci` here to install the pinned published SDK and build tools.
2. Connect your existing application's authenticated `{ userId, getAccessToken, project, database }` to `showAuthenticatedTodos` in `app.mjs`. These are app values, not a new SDK authentication API.
3. Run `npm run dev` and open Vite's URL. Run `npm test` and `npm run build` to verify the example.

Call the returned `dispose()` before logout or account change. Remounting on the same root also clears and aborts the old session. Project credentials never enter the browser. The owner must provision the collection and owner rules outside this app.

The repository's `npm test` installs and tests this example without workflow changes. HTTP-fake tests verify integration behavior only. They do not establish producer rule enforcement, CORS readiness or production proof. The example limits lists to 500 todos and pauses streams after 60 seconds; use **Refresh and reconnect** to resume.
