# Renewing server token provider

Requires Node 22 or 24 and npm 11 or newer. Keep this module and the project key on your server.
Download `server-client.mjs` and `package.json` into the same directory; run `npm install` there.
Supply `TERRASCALE_API_KEY`, `TERRASCALE_PROJECT_ID` and authorized `TERRASCALE_API_URL` privately through your server platform. For the database handle below, also set `TERRASCALE_DATABASE_NAME`.

```js
import { createServerClient } from './server-client.mjs'
const { client } = createServerClient()
const db = client.database(process.env.TERRASCALE_DATABASE_NAME)
```

The published SDK 0.0.17 helper exchanges an explicit project grant without resource. Concurrent renewals coalesce; the cache renews within five seconds of expiry and never falls back to stale credentials after failure. Root SDK data methods resolve the provider for every request/reconnect. Pass cancellation/deadline signals to data operations; the provider callback itself has no signal argument. Authentication failures stop the operation without replaying mutations. Request only the document scopes your application needs.
