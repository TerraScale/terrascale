# JavaScript and TypeScript

Read this for Node.js 22/24 or browser integrations. The examples use current
source, not the previous published `@terrascale/sdk@0.0.17` API.
Sources: [JavaScript quickstart](https://docs.terrascale.tech/getting-started/javascript),
[SDK reference](https://docs.terrascale.tech/guides/sdks).

## Install and configure

In an application directory, using an existing TerraScale monorepo checkout:

```sh
npm init -y
npm install /absolute/path/to/terrascale/sdk/js
```

Record the checkout revision; current source declares `0.0.19`. Do not assume
`npm install @terrascale/sdk` provides this surface. For TypeScript, generate the
source package's declarations with its `npm run build` before consuming types.
The install route follows the [quickstart](https://docs.terrascale.tech/getting-started/javascript).

`createTerraBaseClientFromEnvironment()` reads `TERRASCALE_API_KEY`,
`TERRASCALE_PROJECT_ID`, optional `TERRASCALE_BASE_URL`, and accepts explicit
overrides. An explicit configuration uses `createTerraBaseClient({ project,
apiKey, baseUrl, region, authBaseUrl })`. Omit optional fields unless needed;
`region` pins entry traffic, and a custom API origin with an API key requires
`authBaseUrl`. `token` supplies an already exchanged bearer token instead.
The environment constructor is Node-only. Browser clients use end-user tokens;
never bundle a project API key. Sources:
[quickstart](https://docs.terrascale.tech/getting-started/javascript),
[authentication](https://docs.terrascale.tech/guides/authentication),
[regions](https://docs.terrascale.tech/concepts/regions).

## Complete small example

Provide the three environment variables listed in the skill. Save as
`quickstart.mjs`, then run `node quickstart.mjs`. It creates an example table,
inserts two unique people atomically, reads one and updates it in a session.

```js
import { createTerraBaseClientFromEnvironment, generateId, SqlError } from "@terrascale/sdk";

const name = process.env.TERRASCALE_DATABASE_NAME;
if (!name) throw new Error("Missing TERRASCALE_DATABASE_NAME");
const db = createTerraBaseClientFromEnvironment().database(name);
const signal = AbortSignal.timeout(30_000);
const id = generateId();

await db.execute("CREATE TABLE IF NOT EXISTS sdk_people (id TEXT PRIMARY KEY, name TEXT NOT NULL)", undefined, { signal });
await db.batch([
  { sql: "INSERT INTO sdk_people (id, name) VALUES (?, ?)", args: [id, "Ada"] },
  { sql: "INSERT INTO sdk_people (id, name) VALUES (:id, :name)", named: { id: generateId(), name: "Grace" } },
], { signal });
const rows = await db.query("SELECT id, name FROM sdk_people WHERE id = ?", [id], { signal });
console.log(rows[0].name);

const session = await db.connect({ signal });
try {
  await session.transaction(async tx => {
    const [person] = await tx.query("SELECT name FROM sdk_people WHERE id = ?", [id]);
    if (person.name === "Ada") {
      await tx.execute("UPDATE sdk_people SET name = ? WHERE id = ?", ["Ada Lovelace", id]);
    }
  });
} finally {
  session.close();
}

try {
  await db.execute("INSERT INTO sdk_people (id, name) VALUES (?, ?)", [id, "duplicate"], { signal });
} catch (error) {
  if (!(error instanceof SqlError) || error.code !== "primary_key_violation") throw error;
  console.log(error.code, error.statement, error.sqlstate);
}
```

Expected observations: `Ada` and a `primary_key_violation` at statement `0`,
SQLSTATE `23505`. The transaction callback uses the connected session;
calling `db.execute` there would create an independent REST transaction.
Sources: [quickstart](https://docs.terrascale.tech/getting-started/javascript),
[SQL transactions](https://docs.terrascale.tech/guides/sql-http).

## Results, cancellation and errors

- `query(sql, args, options)` returns objects keyed by column name;
  `execute` returns `columns`, array-valued `rows`, `rowsAffected` and
  `lastInsertRowid`. `batch` returns one such result per statement.
- Named arguments use `{ name: value }`, without the placeholder prefix.
  Blobs use `Uint8Array`. Pass large integers as `bigint`; returned integers
  above 2^53 use `ExactDecimal`, convertible with `BigInt(String(value))`.
- REST methods accept `{ signal }`; for no arguments pass `undefined` before
  that option. `connect({ signal })` controls connection establishment.
  Session statement methods have no per-call `signal` option; do not copy the
  REST options argument onto them.
- `SqlError` has `code`, `statement`, `sqlstate`, `status` and `retryable`.
  `retryable` includes `serialization_conflict`, `tenant_busy`, `unavailable`;
  it is not permission to replay an uncertain write. Current REST source resends
  `serialization_conflict` at most twice, despite older quickstart wording.
- Session `write_outcome_unknown` permanently closes the session. Handle
  `SqlSocketClosedError.closeCode` for other closed sockets; JS sessions do not
  reconnect. Reconcile writes from a new connection before repeating them.

Sources: [JavaScript](https://docs.terrascale.tech/getting-started/javascript),
[errors](https://docs.terrascale.tech/guides/errors).
Source audit: `sdk/js/src/index.js`, `v1/terrabase-v1.js`, `v1/sql-execute.js`,
`v1/sql-socket.js`, `v1/errors.js`, `v1/transport.js` under the revision in
[the skill](../SKILL.md). These files confirm exports, signatures and option names.
