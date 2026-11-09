---
title: Use Drizzle ORM
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Use Drizzle ORM

Drizzle ORM ships a `sqlite-proxy` driver that runs every query through two callbacks. The JavaScript SDK's `@terrascale/sdk/drizzle` export supplies those callbacks from a TerraBase database handle, so Drizzle's SQLite dialect runs over the SDK's SQL API. No `drizzle-orm` dependency is added to the SDK itself.

## Install

```sh
npm install @terrascale/sdk drizzle-orm
```

## Connect

```js
import { drizzle } from "drizzle-orm/sqlite-proxy";
import { createTerraBaseClient } from "@terrascale/sdk";
import { drizzleProxy } from "@terrascale/sdk/drizzle";

const { query, batch } = drizzleProxy(createTerraBaseClient({ project: "my-project", token: process.env.TERRABASE_TOKEN, baseUrl: "https://…" }).database("app"));
const orm = drizzle(query, batch);
```

`query` sends one statement per call; `batch` sends every statement of a Drizzle batch in one request that the server applies as a single transaction: all statements commit or none do.

## Define a schema and query

```js
import { integer, sqliteTable, text } from "drizzle-orm/sqlite-core";
import { eq } from "drizzle-orm";

const users = sqliteTable("users", {
  id: integer("id").primaryKey(),
  name: text("name").notNull(),
});

await orm.insert(users).values({ id: 1, name: "Ada" });

const all = await orm.select().from(users);
const ada = await orm.select().from(users).where(eq(users.name, "Ada"));
```

For multi-statement writes, prefer `orm.batch([...])`: it reaches the server as one atomic transaction. `orm.transaction(...)` over the proxy driver sends its `begin`, statement and `commit` calls as separate requests, so it does not give you an atomic transaction.

## Migrations

Generate SQL migration files with `drizzle-kit generate`, then apply them with `ts migrate` or the SQL API. `drizzle-kit push` and schema introspection are not supported: they need direct catalog access the SQL API does not provide.

## What the adapter does

`drizzleProxy(db)` returns exactly the two callbacks `drizzle-orm/sqlite-proxy` expects:

- `query(sql, params, method)`: sends one statement with `db.execute` and maps the result rows — `all` and `values` return rows as arrays of column values in column order, `get` returns the first row (or no row), `run` returns no rows.
- `batch(queries)`: sends every statement in one `db.batch` request and maps each result the same way.

</div>
