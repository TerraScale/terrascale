# Prisma ORM on TerraBase

Prisma 7 with `@prisma/adapter-libsql` against a TerraBase database: a
schema, a SQL migration, CRUD, relations and transactions.
Guide: https://docs.terrascale.tech/guides/orm-prisma

Install Node 24 (it runs `app.ts` directly), run `npm ci`, then export
`TERRASCALE_API_KEY` (a **Read and write** key for the database),
`TERRASCALE_PROJECT_ID` and `TERRASCALE_DATABASE`. Keep the key in your
secret manager or private shell.

```sh
npm run generate                                # prisma generate
mkdir -p migrations
npm run --silent diff > migrations/0001_init.sql # after a schema change: a new numbered file
npm run migrate                                 # applies migrations/*.sql that are not applied yet
npm start                                       # CRUD, relations and transactions
```

`npm start` inserts the same rows each run: drop the `prisma_authors`, `prisma_posts` and
`prisma_migrations` tables to run it again on a clean database.
