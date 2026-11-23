---
title: JavaScript quickstart
description: Create tables, insert rows and query rows with the JavaScript SDK.
---

# JavaScript quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Node.js 22 or 24
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/getting-started/): the key needs the **sql:read** and **sql:write** scopes on that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

`@terrascale/sdk@0.0.17` is the previous published package. The current source is version `0.0.19` in `sdk/js/package.json`. This page matches that source. Install the package from a checkout of the repository:

```bash
mkdir quickstart && cd quickstart
npm init -y
npm install /path/to/terrascale/sdk/js
```

## Run the quickstart

Save this as `quickstart.mjs`. Pass the dashboard key as `apiKey`; the client exchanges it. SQL goes through `execute`, `query` and `batch` on the database.

```js
import { createTerraBaseV1Client, generateId, SqlError } from "@terrascale/sdk";

const required = (name) => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: required("TERRASCALE_PROJECT_ID"),
  apiKey: required("TERRASCALE_API_KEY"),
});
const db = client.database(required("TERRASCALE_DATABASE_NAME"));

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

The quickstart is valid TypeScript as written; `strict` mode needs one annotation
on the helper parameter (`const required = (name: string) => {`).

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: primary_key_violation statement 0 sqlstate 23505
```

## How it works

- **The key never reaches TerraBase.** `apiKey` makes the client exchange the dashboard key with ts-auth for a short-lived project access token and refresh it from `expires_in`. Keep the key on your server; see [authentication](/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional (`?`) arrays and named objects are sent as typed JSON. Integers above 2^53 decode as an `ExactDecimal`; pass large integers as `bigint`.
- **Refused statements throw `SqlError`.** It carries `code` (a TerraScale code such as `primary_key_violation`), `sqlstate`, the failing `statement` (zero-based) and `retryable`; the whole request rolled back and nothing is retried automatically.

## Next steps

- [SDK reference](/guides/sdks) — the five SDKs and their install commands
- [C#](/getting-started/csharp), [Go](/getting-started/go), [Python](/getting-started/python) and [Rust](/getting-started/rust) quickstarts
- [SQL over HTTP](/guides/sql-http) for the raw HTTP interface and [concepts](/concepts/sql) for the SQL dialect
- [Use Drizzle ORM](/guides/orm-drizzle) and [Use Prisma ORM](/guides/orm-prisma) for ORMs over the JavaScript SDK
