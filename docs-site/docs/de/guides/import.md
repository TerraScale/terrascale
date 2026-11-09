---
title: Import data
description: Load existing data into a TerraScale database with batched SQL statements.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Import data

There is no file upload or restore endpoint: data enters a TerraScale database as SQL statements, through [SQL over HTTP](/de/guides/sql-http), the [SDKs](/de/guides/sdks) or the [Postgres wire](/de/guides/postgres-clients). Create the tables first, then send the rows in batches that fit the request limits.

## With the CLI

`ts db import` loads a SQL script into a database for you, splitting it into batches that fit the limits below. Export one database and load it into another:

```sh
ts db export source-db -o dump.sql
ts db import target-db dump.sql
```

`-` reads the script from stdin, so `ts db export source-db | ts db import target-db -` also works, and any SQLite-dialect `.sql` file can be imported the same way. The import is not atomic: each batch commits on its own. If a statement fails, the CLI prints the 1-based number of the first statement of the failed batch and you resume with `-from <n>`, which skips the first n-1 statements.

## Load rows in batches

One `POST /v1/projects/{project}/databases/{database}/sql/execute` carries 1 to 256 statements and runs them as one transaction, all or nothing. Give each row its own `INSERT` with bound parameters, and keep every batch inside the limits: at most 256 statements, each at most 16 KiB, a body of at most 1 MiB and at most 1,024 rows changed — past them the request answers `413 too_large`. Writes need the `sql:write` scope.

```bash
curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' \
  -d '{"statements":[
        {"sql":"INSERT INTO customers (email, seats) VALUES (?, ?)","args":["team@example.com",12]},
        {"sql":"INSERT INTO customers (email, seats) VALUES (?, ?)","args":["ops@example.com",3]}
      ]}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

The access token comes from a project API key, as in [SQL over HTTP](/de/guides/sql-http). Repeat the request for each batch of rows: a batch that fails rolls back only itself, so it can be retried whole. See [errors](/de/guides/errors) for which failures are safe to retry.

## From another TerraScale database

The [export](/de/guides/sql-http#export-a-database) endpoint returns one SQL script of the whole source database — schema, rows, indexes, views and triggers — from a single read snapshot. Split that script into batches within the limits above and `POST` them to the target database; the statements are plain SQLite SQL and run unchanged.

## Coming from PostgreSQL

Statements use the SQLite dialect, and Postgres-only syntax is refused over HTTP. A `pg_dump --data-only --inserts --column-inserts` dump must be converted to SQLite SQL first — table definitions are not translated. The [Postgres wire](/de/guides/postgres-clients) accepts a few Postgres spellings (`SERIAL`, `ILIKE`, `now()`, `expr::type`), but only on that transport, and `COPY … FROM STDIN` and psql's `\copy` are not supported there either: the wire serves queries only. Convert the dump's `INSERT` statements to batches like the ones above.

</div>
