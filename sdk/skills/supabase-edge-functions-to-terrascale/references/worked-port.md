# Worked port: page visit count

This example ports a read-only handler after the `visits` table has been migrated.
It deliberately keeps authentication at an existing application gateway, which
permits this aggregate to the signed-in caller. Review that decision for your
application; it is not an example of per-user data isolation.

## Before: Supabase Deno handler and browser caller

The source uses [Deno Edge Functions](https://supabase.com/docs/guides/functions),
[environment secrets](https://supabase.com/docs/guides/functions/secrets),
[select/count](https://supabase.com/docs/reference/javascript/select) and
[functions.invoke](https://supabase.com/docs/reference/javascript/functions-invoke).
Its existing Supabase gateway authenticates callers. Keep that configuration
alongside the source so rollback restores the same access rules.

`supabase/functions/visit-count/index.ts`:

```ts
import { createClient } from 'npm:@supabase/supabase-js@2';

Deno.serve(async request => {
  const body = await request.json();
  if (typeof body?.page !== 'string' || body.page.length < 1 || body.page.length > 200) {
    return Response.json({ error: 'invalid_page' }, { status: 400 });
  }
  const db = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!);
  const { count, error } = await db.from('visits').select('page', { count: 'exact', head: true }).eq('page', body.page);
  if (error) return Response.json({ error: 'query_failed' }, { status: 500 });
  return Response.json({ page: body.page, visits: count });
});
```

```js
const { data, error } = await supabase.functions.invoke('visit-count', {
  body: { page: 'home' },
});
if (error) throw error;
renderCount(data.visits);
```

## After: one JavaScript file, no imports or environment secrets

Save as `handler.js`. The global `sql`, `handler(event)` entry, positional
parameters and return shape are defined by the
[TerraScale Functions guide](https://docs.terrascale.tech/guides/functions).
Remove the Supabase service key from this deployment: database access uses the
function's own authority, rather than an environment credential.

```js
async function handler(event) {
  if (event.trigger !== 'http') return { status: 400, body: { error: 'http_required' } };
  const page = event.body?.page;
  if (typeof page !== 'string' || page.length < 1 || page.length > 200) {
    return { status: 400, body: { error: 'invalid_page' } };
  }
  const rows = await sql.query('SELECT count(*) AS visits FROM visits WHERE page = ?', [page]);
  return { status: 200, body: { page, visits: rows[0].visits } };
}
```

Deploy with the parent skill's PUT command, setting `FUNCTION_NAME=visit-count`.
This handler has no cron branch; never add a schedule to it as written.
For a scheduled function, explicitly handle `event.trigger === 'cron'` and use
`event.scheduledAt` as documented in the
[event contract](https://docs.terrascale.tech/guides/functions).

## Verify a filled staging case

In an isolated staging database, create the fixture with the documented
[SQL HTTP API](https://docs.terrascale.tech/guides/sql-http). This is disposable
example data; do not insert it into a production database.

```json
{
  "statements": [
    {"sql": "CREATE TABLE visits (id INTEGER PRIMARY KEY, page TEXT NOT NULL)"},
    {"sql": "INSERT INTO visits (id, page) VALUES (?, ?)", "args": [1, "home"]},
    {"sql": "INSERT INTO visits (id, page) VALUES (?, ?)", "args": [2, "home"]},
    {"sql": "INSERT INTO visits (id, page) VALUES (?, ?)", "args": [3, "about"]}
  ]
}
```

Save as `fixture.json`. With a fresh access token:

```bash
curl -sS --fail --header @/dev/fd/3 \
  -H 'Content-Type: application/json' --data-binary @fixture.json \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/$DATABASE_NAME/sql/execute" \
  -o fixture-result.json \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
curl -sS --fail --header @/dev/fd/3 \
  -H 'Content-Type: application/json' -d '{"page":"home"}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/$DATABASE_NAME/functions/visit-count/invoke" \
  -o invoke-result.json \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
jq -e '.page == "home" and .visits == 2' invoke-result.json
```

The invoke route and result shape follow
[deploy and call](https://docs.terrascale.tech/guides/functions). If the assertion
fails, inspect the safe HTTP status, deployed source and table data before
changing traffic. Also verify `about` returns 1, an absent page returns 0, an empty
page returns HTTP 400 with `invalid_page`, and denied gateway users cannot cause
an invocation. Verify the invocation's SQL call count and response size in logs.

## Replace the caller at the application boundary

Create an application-owned `/api/visit-count` endpoint using your backend's
existing authenticated routing. That route must validate session, authorize
access to the aggregate, validate `page`, obtain a fresh TerraScale access token
and issue the same POST above. The backend alone holds project credentials;
follow [server authentication](https://docs.terrascale.tech/guides/authentication).
Return the upstream HTTP status and JSON through a reviewed error handler;
do not expose authorization headers or raw platform diagnostic data.

The application endpoint is code you implement, not a TerraScale route. Replace
the browser call after that endpoint is deployed and verified:

```js
const response = await fetch('/api/visit-count', {
  method: 'POST',
  credentials: 'same-origin',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ page: 'home' }),
});
if (!response.ok) throw new Error(`Visit count failed (${response.status})`);
const data = await response.json();
renderCount(data.visits);
```

Use the application's existing CSRF protections for cookie-authenticated
endpoints and apply its CORS policy at that gateway. Keep the old caller behind
the release switch until validation passes. Rollback switches the application
route back to Supabase and preserves any migrated database changes deliberately;
this read-only example itself introduces no new business writes.
