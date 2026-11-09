---
title: Use Prisma ORM
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Use Prisma ORM

Prisma's driver adapters let the client run on any database backend. The JavaScript SDK's `@terrascale/sdk/prisma` export supplies that adapter from a TerraBase database handle, so Prisma's SQLite dialect runs over the SDK's SQL API. No `@prisma/*` dependency is added to the SDK itself.

## Install

```sh
npm install @terrascale/sdk @prisma/client
```

The `@terrascale/sdk/prisma` export ships in the next SDK release; until then install it from source (the monorepo's `sdk/js`).

## Connect

Point Prisma at the SQLite provider and pass the adapter to the client constructor:

```prisma
datasource db {
  provider = "sqlite"
}
```

```js
import { PrismaClient } from "@prisma/client";
import { createTerraBaseClient } from "@terrascale/sdk";
import { PrismaTerraScale } from "@terrascale/sdk/prisma";

const db = createTerraBaseClient({ project: "my-project", token: process.env.TERRABASE_TOKEN, baseUrl: "https://…" }).database("app");
const prisma = new PrismaClient({ adapter: new PrismaTerraScale(db) });
```

With Prisma 7 no `previewFeatures` entry and no `url` in the datasource block are needed: the adapter given to the constructor is the connection.

## Query

```js
await prisma.person.create({ data: { id: 1, name: "Ada", score: 1.5 } });
const all = await prisma.person.findMany();
```

Every query is one SQL API request. Interactive transactions work:

```js
await prisma.$transaction(async tx => {
  await tx.person.create({ data: { id: 2, name: "Grace", score: 0 } });
});
```

An interactive transaction runs over one dedicated SQL WebSocket session, and the server ends each transaction after 5 seconds, so keep the callback short.

## Errors

The adapter maps the server's SQLSTATE to Prisma's error kinds: unique violations to P2002, not-null violations to P2011, foreign-key violations to P2003, missing tables to P2021 and missing columns to P2022. Every other refusal keeps the server's message and surfaces as a generic database error.

## Migrations

Prisma does not run migrations here: `prisma migrate dev` and `prisma db push` (and the shadow database they need) are not supported. Generate SQL instead and apply it yourself:

```sh
prisma migrate diff --from-empty --to-schema prisma/schema.prisma --script > migration.sql
```

For later changes, diff against the applied migrations with `--from-migrations prisma/migrations`. Apply the generated script with `ts migrate` or the SQL API.

## What the adapter does

`PrismaTerraScale(database)` returns Prisma's driver-adapter interface:

- `queryRaw`/`executeRaw`: one statement per SQL API request, with SQLite column types mapped from the server's declared types.
- `executeScript`: a generated migration script applied as one atomic batch.
- `startTransaction`: an interactive transaction over a dedicated SQL WebSocket session, with savepoint operations and a 5-second server-side limit.

</div>
