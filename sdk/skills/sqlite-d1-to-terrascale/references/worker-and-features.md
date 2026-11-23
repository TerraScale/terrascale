# SQLite and D1 feature mapping

Return to the [workflow](../SKILL.md) for export, import and rollback.

| Source feature | TerraScale equivalent or workaround |
| --- | --- |
| Local `.db` / `.sqlite` file | Dump with `sqlite3` and import SQL; no file upload, attachment, or replica. See [import](https://docs.terrascale.tech/guides/import). |
| Tables, indexes, views, triggers | Supported SQL objects, subject to statement restrictions and enforced foreign keys. Rehearse load ordering and trigger effects; see [SQL](https://docs.terrascale.tech/concepts/sql). |
| Extension functions, virtual/temporary tables, `ATTACH` | Virtual/temporary tables and attachment are refused. Materialize supported tables or move the operation into application code; extension compatibility is not promised by [SQL](https://docs.terrascale.tech/concepts/sql). |
| D1 `env.DB` binding | Replace with authenticated [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http). No destination D1 binding compatibility is documented. |
| `prepare().bind().all()` | Send `{sql, args}` in `statements`; map column metadata plus array rows to application objects. See [HTTP response](https://docs.terrascale.tech/guides/sql-http). |
| D1 `.batch()` | One HTTP request is one atomic batch, with result entries in statement order; respect [limits](https://docs.terrascale.tech/limits). |
| D1 `.first()`, `.run()`, `exec()` result metadata | Select the first mapped row or inspect `rows_affected` / `last_insert_rowid` as appropriate; do not assume D1 `meta` fields exist. HTTP accepts statements, not arbitrary scripts; see [HTTP](https://docs.terrascale.tech/guides/sql-http). |
| D1 Sessions/bookmarks | No D1 session bookmark compatibility is documented in [SQL](https://docs.terrascale.tech/concepts/sql). Revisit the consistency requirement against the documented transport/transaction model. |
| D1 migration ledger | Keep application migration ordering and ownership explicit; source bookkeeping is not a destination migration protocol. See destination [migrations](https://docs.terrascale.tech/guides/migrations). |
| D1 Time Travel / SQLite backup files | Establish new destination [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore). Source history and files are not imported as recovery history. |

Source APIs: [SQLite CLI](https://sqlite.org/cli.html),
[D1 database](https://developers.cloudflare.com/d1/worker-api/d1-database/),
[prepared statements](https://developers.cloudflare.com/d1/worker-api/prepared-statements/),
and [Time Travel](https://developers.cloudflare.com/d1/reference/time-travel/).

## Worker before and after

These are functions called from an existing authenticated Worker handler after it
has authorized `id` and validated `email`. They assume a migrated
`customers(id TEXT PRIMARY KEY, email TEXT NOT NULL)` table. Keep database routing
in trusted configuration; never accept it directly from a request parameter.

Before, with [D1 prepared statements and batches](https://developers.cloudflare.com/d1/worker-api/d1-database/):

```js
async function readCustomer(env, id) {
  const result = await env.DB.prepare(
    "SELECT id, email FROM customers WHERE id = ?",
  ).bind(id).all();
  return result.results;
}

async function updateCustomer(env, id, email) {
  const results = await env.DB.batch([
    env.DB.prepare("UPDATE customers SET email = ? WHERE id = ?").bind(email, id),
    env.DB.prepare("SELECT id, email FROM customers WHERE id = ?").bind(id),
  ]);
  return results[1].results;
}
```

After, using only Worker `fetch` and the documented
[key exchange](https://docs.terrascale.tech/guides/authentication) and
[SQL HTTP contract](https://docs.terrascale.tech/guides/sql-http):

```js
async function accessToken(env) {
  const key = env.TERRASCALE_API_KEY;
  if (!key) throw new Error("Missing TERRASCALE_API_KEY");
  const unprefixed = key.startsWith("sk_") ? key.slice(3) : key;
  const separator = unprefixed.indexOf("_");
  if (separator < 1) throw new Error("Invalid API key format");
  const keyId = unprefixed.slice(0, separator);
  const response = await fetch("https://auth.terrascale.tech/terrascale/oauth/token", {
    method: "POST",
    headers: {
      Authorization: `Basic ${btoa(`${keyId}:${key}`)}`,
      "Content-Type": "application/x-www-form-urlencoded",
    },
    body: new URLSearchParams({ grant_type: "client_credentials" }),
  });
  if (!response.ok) throw new Error(`Token exchange failed: ${response.status}`);
  const body = await response.json();
  if (typeof body.access_token !== "string" || !body.access_token) {
    throw new Error("Missing access token");
  }
  return body.access_token;
}

async function execute(env, statements) {
  const token = await accessToken(env);
  const project = encodeURIComponent(env.TERRASCALE_PROJECT_ID);
  const database = encodeURIComponent(env.TERRASCALE_DATABASE_NAME);
  const response = await fetch(
    `https://api.terrascale.tech/v1/projects/${project}/databases/${database}/sql/execute`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ statements }),
    },
  );
  if (!response.ok) {
    const failure = await response.json();
    const code = failure.error?.code ?? "sql_request_failed";
    throw new Error(`SQL request failed: ${response.status} ${code}`);
  }
  return (await response.json()).results;
}

function objects(result) {
  return result.rows.map(row => Object.fromEntries(
    result.columns.map((column, index) => [column.name, row[index]]),
  ));
}

async function readCustomer(env, id) {
  const results = await execute(env, [
    { sql: "SELECT id, email FROM customers WHERE id = ?", args: [id] },
  ]);
  return objects(results[0]);
}

async function updateCustomer(env, id, email) {
  const results = await execute(env, [
    { sql: "UPDATE customers SET email = ? WHERE id = ?", args: [email, id] },
    { sql: "SELECT id, email FROM customers WHERE id = ?", args: [id] },
  ]);
  return objects(results[1]);
}
```

This minimal example exchanges once per operation so it never holds a stale token
between Worker invocations. A production cache may reuse a token until before its
`expires_in` deadline, bounded by key expiry. Tokens live at most 60 seconds;
see [authentication](https://docs.terrascale.tech/guides/authentication).
Do not log tokens, keys, authorization headers or raw response bodies. Return a
controlled application error from the handler. On a network failure or uncertain
commit, check the operation’s business key before replaying a mutation; see
[errors](https://docs.terrascale.tech/guides/errors).

The sample selects only text fields. For blobs use the HTTP `{base64: ...}` shape;
for integers above JavaScript’s exact range use a lossless strategy rather than
silently converting them to `number`. Review the
[typed HTTP values](https://docs.terrascale.tech/guides/sql-http) and
[SDK large-integer guidance](https://docs.terrascale.tech/getting-started/javascript)
when adapting it to other tables.

Supply `TERRASCALE_PROJECT_ID` and `TERRASCALE_DATABASE_NAME` as trusted Worker
configuration. Enter the key through the interactive prompt:

```bash
npx wrangler secret put TERRASCALE_API_KEY
```

For a named environment, append the matching `--env` to the secret command and
deployment commands. This command creates and deploys a Worker version; stage it
within the rollout plan. For gradual deployment use the versioned-secret path in
[Worker secrets](https://developers.cloudflare.com/workers/configuration/secrets/).
Keep development secret files ignored by Git. Remove `env.DB` only after all
callers have moved and the rollback plan retains the old binding configuration.

## Prepare the CLI artifact

The [public import guide](https://docs.terrascale.tech/guides/import) shows raw
`.dump` input, but the reviewed CLI contradicts that shortcut:
[import.go](https://github.com/TerraScale/terrascale/blob/6b786568d60373e2fa16836a8ab4354bbb642651/sdk/go/cmd/ts/import.go#L31)
forwards split statements unchanged, and
[splitSQL](https://github.com/TerraScale/terrascale/blob/6b786568d60373e2fa16836a8ab4354bbb642651/sdk/go/cmd/ts/migrate.go#L398)
splits trigger bodies at their internal semicolons. The
[HTTP contract](https://docs.terrascale.tech/guides/sql-http) refuses transaction
wrappers; [SQL](https://docs.terrascale.tech/concepts/sql) always enforces foreign
keys. Do not assume the CLI strips controls or preserves complete trigger DDL.

Before any CLI import, use a SQL-aware parser or review complete statements:

1. Keep the original successful export unchanged. Reject truncated/failed dumps
   and dumps that end in `ROLLBACK`; do not turn an aborted dump into a partial load.
2. Remove complete top-level dump `BEGIN`/`COMMIT` controls and all source `PRAGMA`
   statements from the import copy. Inspect each removed pragma for behavior that
   needs a schema/load-order change. Never strip words inside strings or triggers.
3. Extract every complete `CREATE TRIGGER ... BEGIN ... END;` to its own reviewed
   file. Do not put trigger DDL into the CLI artifact, even if it is supported SQL.
4. Produce `source.sql` containing reviewed tables, inserts, indexes and views,
   ordered for enforced foreign keys. Resolve other unsupported source statements.
   Verify there are no dump controls, pragmas or trigger declarations left.
5. Rehearse this exact artifact through the installed CLI into an empty target.
   After importing and verifying rows, submit each complete trigger as one SDK/HTTP
   statement, within the [statement limits](https://docs.terrascale.tech/limits).
   Keep separate progress for trigger installation; a failed DDL call does not undo
   already committed data batches.

For example, given source dump controls, an audit table, a customer table, data,
and an audit trigger, the prepared CLI artifact can contain only:

```sql
CREATE TABLE customers (id TEXT PRIMARY KEY, email TEXT NOT NULL);
CREATE TABLE audit (id TEXT NOT NULL);
INSERT INTO customers VALUES ('c1', 'team@example.com');
```

After row verification, submit the entire following string as one statement.
Its internal semicolon stays inside the string sent to the SQL endpoint:

```js
await execute(env, [{
  sql: "CREATE TRIGGER customer_audit AFTER INSERT ON customers BEGIN INSERT INTO audit(id) VALUES (new.id); END;",
}]);
```

Here `execute` is the HTTP helper above. Run this once from a controlled migration
entry point with the destination secret environment, never on normal requests.
Verify subsequent customer inserts create exactly one expected audit row. Check
for an existing trigger before resuming an uncertain DDL outcome.

## Recovery comparison

D1 [Time Travel](https://developers.cloudflare.com/d1/reference/time-travel/)
uses bookmarks/timestamps for the source recovery window. A SQL export contains
current data and schema, not those bookmarks or historical versions.

Destination [backups and restore](https://docs.terrascale.tech/guides/backups-and-restore)
support manual/scheduled snapshots, point-in-time restore into a new database,
and an explicit in-place rewind. Read the actual restore window first with
`GET /v1/projects/{project}/databases/{database}/point-in-time?destination=platform`.
The proven window and [plan retention](https://docs.terrascale.tech/limits), rather
than a source bookmark, determine which instants can be restored.

Use restore-to-new for a rehearsal: request the documented snapshot/PITR restore,
check its operation status to completion, then validate actual recovered rows.
Do not repoint the application at an operation still marked `running`.
A new copy needs a free database slot. Rewind keeps the same database identity
and credentials but discards later changes; obtain explicit consent for the exact
database and time before invoking it. The route/status/consent contract is in
[backups and restore](https://docs.terrascale.tech/guides/backups-and-restore).
