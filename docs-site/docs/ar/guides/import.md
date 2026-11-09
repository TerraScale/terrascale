---
title: Import data
description: Load existing data into a TerraScale database with batched SQL statements.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Import data

There is no file upload or restore endpoint: data enters a TerraScale database as SQL statements, through [SQL over HTTP](/ar/guides/sql-http), the [SDKs](/ar/guides/sdks) or the [Postgres wire](/ar/guides/postgres-clients). Create the tables first, then send the rows in batches that fit the request limits.

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

The access token comes from a project API key, as in [SQL over HTTP](/ar/guides/sql-http). Repeat the request for each batch of rows: a batch that fails rolls back only itself, so it can be retried whole. See [errors](/ar/guides/errors) for which failures are safe to retry.

## From another TerraScale database

The [export](/ar/guides/sql-http#export-a-database) endpoint returns one SQL script of the whole source database — schema, rows, indexes, views and triggers — from a single read snapshot. Split that script into batches within the limits above and `POST` them to the target database; the statements are plain SQLite SQL and run unchanged.

## Coming from PostgreSQL

Statements use the SQLite dialect, and Postgres-only syntax is refused over HTTP. A `pg_dump --data-only --inserts --column-inserts` dump must be converted to SQLite SQL first — table definitions are not translated. The [Postgres wire](/ar/guides/postgres-clients) accepts a few Postgres spellings (`SERIAL`, `ILIKE`, `now()`, `expr::type`), but only on that transport, and `COPY … FROM STDIN` and psql's `\copy` are not supported there either: the wire serves queries only. Convert the dump's `INSERT` statements to batches like the ones above.

</div>
