# Browser todos

**Pending:** SDK 0.0.5 publication (0.0.4 is currently latest), app-owned sign-in integration, collection-rule rollout, and live authorization proof. No hosted login or credential-entry form is included.

Read [the browser todo guide](../../docs/getting-started/browser-todos.md) for server-owner rules, the end-user session contract and live verification requirements.

1. After SDK 0.0.5 is published, run `npm install` here. Commit the resulting real registry lockfile with the coordinated release.
2. Connect your existing application's authenticated `{ userId, getAccessToken, project, database }` to `showAuthenticatedTodos` in `app.mjs`. These are app values, not a new SDK authentication API.
3. Run `npm run dev` and open Vite's URL. Run `npm test` and `npm run build` to verify the example.

Call the returned `dispose()` before logout or account change. Remounting on the same root also clears and aborts the old session. Project credentials never enter the browser. The owner must provision the collection and planned rules outside this app.

The repository's `npm test` installs and tests this example without workflow changes. That CI step requires the real SDK 0.0.5 publication. HTTP-fake tests verify integration behavior only. They do not establish producer rule enforcement, CORS readiness or production proof. The example limits lists to 500 todos and pauses streams after 60 seconds; use **Refresh and reconnect** to resume.
