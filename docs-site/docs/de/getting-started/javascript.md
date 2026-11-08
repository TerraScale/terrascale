---
title: JavaScript quickstart
description: Create tables, insert rows and query rows with the JavaScript SDK.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# JavaScript quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Node.js 22 or 24
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/de/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

```bash
mkdir quickstart && cd quickstart
npm init -y
npm install @terrascale/sdk@0.0.19
```

## Run the quickstart

Save this as `quickstart.mjs`. The client exchanges the key for a short-lived project access token and refreshes it on its own.

```js
import { createTerraBaseV1Client, generateId, SqlError } from "@terrascale/sdk";

const required = (name) => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const project = required("TERRASCALE_PROJECT_ID");
const apiKey = required("TERRASCALE_API_KEY");
const databaseName = required("TERRASCALE_DATABASE_NAME");

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project,
  apiKey,
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
// batch returns one result per statement; each result's rows are arrays by column.
const batch = await db.batch([
  { sql: "INSERT INTO people (id, name) VALUES (:id, :name)", named: { id: generateId(), name: "Grace" } },
  "SELECT count(*) AS n FROM people",
]);
console.log("batch inserted, people count:", Number(batch[1].rows[0][0]));

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

To check types on a TypeScript copy, add a `tsconfig.json` and run the compiler
in no-emit mode:

```bash
npm install --save-dev typescript @types/node
cp quickstart.mjs quickstart.ts
npx tsc --noEmit
```

```json
{
  "compilerOptions": {
    "target": "es2022",
    "module": "esnext",
    "moduleResolution": "bundler",
    "lib": ["es2023"],
    "types": ["node"],
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true
  },
  "include": ["quickstart.ts"]
}
```

The quickstart is valid TypeScript as written; `strict` mode needs one annotation:
the helper parameter (`const required = (name: string) => {`).

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: primary_key_violation statement 0 sqlstate 23505
```

## How it works

- **The key never reaches TerraBase.** The client exchanges it with ts-auth for a 60-second project access token and refreshes it before it expires. Keep the key on your server; see [authentication](/de/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/de/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional (`?`) arrays and named objects are sent as typed JSON. Integers above 2^53 decode as an `ExactDecimal`; pass large integers as `bigint`.
- **Refused statements throw `SqlError`.** It carries `code` (a TerraScale code such as `primary_key_violation`), `sqlstate`, the failing `statement` (zero-based) and `retryable`; the whole request rolled back and nothing is retried automatically.

## Next steps

- [SDK reference](/de/guides/sdks) — the five SDKs and their install commands
- [C#](/de/getting-started/csharp), [Go](/de/getting-started/go) and [Python](/de/getting-started/python) quickstarts
- [SQL over HTTP](/de/guides/sql-http) for the raw HTTP interface and [concepts](/de/concepts/sql) for the SQL dialect

</div>
