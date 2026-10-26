# Drizzle ORM on TerraBase

Drizzle with `@libsql/client` against a TerraBase database: a schema, a
`drizzle-kit` migration, CRUD, a relational query, a join and transactions.
Guide: https://docs.terrascale.tech/guides/orm-drizzle

Install Node 22 or 24, run `npm ci`, then export `TERRASCALE_API_KEY` (a
**Read and write** key for the database), `TERRASCALE_PROJECT_ID` and
`TERRASCALE_DATABASE`. Keep the key in your secret manager or private shell.

```sh
npm run generate   # drizzle-kit generate, after a schema change
npm run migrate    # applies drizzle/*.sql that are not applied yet
npm start          # CRUD, relations and transactions
```

`npm start` inserts the same rows each run: drop the `drizzle_authors` and `drizzle_posts`
tables (and `drizzle_migrations`) to run it again on a clean database.
