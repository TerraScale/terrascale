---
title: JavaScript quickstart
description: Create tables, insert rows and query rows with the JavaScript SDK.
---

# JavaScript quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Node.js 22 or 24
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The SQL methods on the database handle are newer than the latest npm release (`0.0.17` lacks blobs, `shape(table)` and later fixes), so install the package from a monorepo checkout for now:

```bash
git clone --depth 1 https://github.com/TerraScale/terrascale.git
cd terrascale/sdk/js && npm ci --ignore-scripts && npm run build && npm pack
mkdir quickstart && cd quickstart
npm init -y
npm install ../path/to/terrascale/sdk/js/terrascale-sdk-0.0.19.tgz
```

`npm pack` writes a tarball (`terrascale-sdk-<version>.tgz`) you can install from anywhere; it carries the SDK's `ulid` dependency with it.

## Run the quickstart

Save this as `quickstart.mjs`. The program exchanges the key for a short-lived access token itself; every other operation goes through the client.

```js
import { createTerraBaseV1Client, generateId, SqlError } from "@terrascale/sdk";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";

const required = (name) => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const project = required("TERRASCALE_PROJECT_ID");
const apiKey = required("TERRASCALE_API_KEY");
const databaseName = required("TERRASCALE_DATABASE_NAME");

// The SDK ships no key exchange: exchange the dashboard key for a short-lived
// project access token (60 seconds) here and hand the client a token provider.
const auth = createTsAuthClient({
  apiBaseUrl: "https://auth.terrascale.tech",
  instanceSlug: "terrascale",
});
const separator = apiKey.indexOf("_");
if (separator < 1) throw new Error("Expected a dashboard API key.");
const keyId = apiKey.slice(0, separator);
let cached;
let expiresAt = 0;
async function getAccessToken() {
  if (cached !== undefined && expiresAt - performance.now() > 5000) return cached;
  const result = await auth.oidc.token(keyId, apiKey, {
    grant_type: "client_credentials",
    project,
    scope: "sql:read sql:write",
  });
  if (!result.ok) throw new Error("Token exchange failed; check the key's permissions.");
  cached = result.value.access_token;
  expiresAt = performance.now() + result.value.expires_in * 1000;
  return cached;
}

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project,
  token: getAccessToken,
});
const db = client.database(databaseName);

// One statement, one transaction. IF NOT EXISTS makes the quickstart safe to run twice.
await db.execute("CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)");

// Insert with parameters; generateId() mints a ULID primary key.
const ada = generateId();
await db.execute("INSERT INTO people (id, name) VALUES (?, ?)", [ada, "Ada"]);

// query returns rows as objects keyed by column name.
const people = await db.query("SELECT id, name FROM people WHERE id = ?", [ada]);
console.log("query matched", people[0].id, people[0].name);

// A batch of 1 to 256 statements runs as one transaction: all commit or none do.
const { rowsAffected } = await db.batch([
  { sql: "INSERT INTO people (id, name) VALUES (:id, :name)", named: { id: generateId(), name: "Grace" } },
  "SELECT count(*) AS n FROM people",
]);
const counted = await db.query("SELECT count(*) AS n FROM people");
console.log("batch inserted, people count:", Number(counted[0].n));

// A refused statement throws SqlError; the whole request rolled back.
try {
  await db.execute("INSERT INTO people (id, name) VALUES (?, 'dup')", [ada]);
} catch (error) {
  if (!(error instanceof SqlError)) throw error;
  console.log("refused:", error.code, "statement", error.statement, "sqlstate", error.sqlstate);
}
```

Run it with the key exported in your shell:

```bash
node quickstart.mjs
```

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: primary_key_violation statement 0 sqlstate 23505
```

## How it works

- **The key never reaches TerraBase.** The program exchanges it with ts-auth for a 60-second project access token; `token: getAccessToken` hands the client a fresh one whenever it needs it. Keep the key on your server; see [authentication](/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional (`?`) arrays and named objects are sent as typed JSON; `Uint8Array` blobs travel as base64 and come back as `Uint8Array`. Integers above 2^53 decode as an `ExactDecimal`; pass large integers as `bigint`.
- **Refused statements throw `SqlError`.** It carries `code` (a TerraScale code such as `primary_key_violation`), `sqlstate`, the failing `statement` (zero-based) and `retryable`; the whole request rolled back and nothing is retried automatically.

## Next steps

- [SDK reference](/guides/sdks) — the five SDKs and their install commands
- [C#](/getting-started/csharp), [Go](/getting-started/go) and [Python](/getting-started/python) quickstarts
- [SQL over HTTP](/guides/sql-http) for the raw HTTP interface and [concepts](/concepts/sql) for the SQL dialect
