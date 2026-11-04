---
title: Functions
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Functions

A function is one JavaScript file deployed next to a database. It runs on an HTTPS call or a cron schedule and reads and writes that database with SQL.

## Write a function

Define `handler(event)`. The `sql` binding runs statements on the function's own database:

```js
async function handler(event) {
  await sql.execute("CREATE TABLE IF NOT EXISTS visits (id INTEGER PRIMARY KEY, page TEXT NOT NULL)");
  await sql.execute("INSERT INTO visits (page) VALUES (?)", [event.body.page]);
  const rows = await sql.query("SELECT page, count(*) AS visits FROM visits GROUP BY page ORDER BY visits DESC LIMIT 5");
  return { status: 200, body: rows };
}
```

| Call | Answers |
| --- | --- |
| `sql.execute(text, args?)` | `{columns, rows, rowsAffected, lastInsertRowid}`; rows are arrays |
| `sql.query(text, args?)` | Rows as objects keyed by column name |
| `sql.batch([{sql, args?, named?}, ...])` | One result per statement, all in one transaction |
| `sql.transaction(async tx => ...)` | `tx.execute`, `tx.query` and `tx.batch` in one transaction; commits when the callback returns, rolls back when it throws |

`args` is an array of positional values (`?`) or an object of named values (`:name`). Every call returns a promise. A call outside `sql.transaction` commits on its own.

```js
async function handler(event) {
  return sql.transaction(async tx => {
    await tx.execute("UPDATE accounts SET balance = balance - :amount WHERE id = :from", event.body);
    await tx.execute("UPDATE accounts SET balance = balance + :amount WHERE id = :to", event.body);
    return { status: 204 };
  });
}
```

Statements use the same SQL as the [SQL API](/ja/concepts/sql), with the same sandbox and limits. A refused statement throws an error with the REST error fields:

```js
try {
  await sql.execute("INSERT INTO visits (id, page) VALUES (1, 'home')");
} catch (error) {
  // error.code "primary_key_violation", error.sqlstate "23505", error.statement 0, error.message
}
```

The handler returns `{status, body}`, or any JSON value for status 200. `event` is `{trigger: "http", body, query}` for HTTPS calls and `{trigger: "cron", scheduledAt}` for cron runs. `console.log` lines go to the function's logs.

## Deploy and call

```bash
jq -n --rawfile source visits.js '{source: $source}' | curl -sS --fail-with-body --header @/dev/fd/3 \
  -X PUT -H 'Content-Type: application/json' --data-binary @- \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/functions/visits" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")

curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' -d '{"page":"home"}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/functions/visits/invoke" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

Add `"cron": "*/5 * * * *"` (UTC) to the deploy body to run on a schedule. Deploying, reading logs and deleting need a key with read and write access; invoking needs read access. The function's SQL runs with the function's own authority.

| API (under `/v1/projects/{project}/databases/{database}`) | Purpose |
| --- | --- |
| `PUT /functions/{name}` `{source, cron?}` | Deploy or replace |
| `GET /functions`, `GET /functions/{name}` | List, read with source |
| `POST /functions/{name}/invoke` | Run |
| `GET /functions/{name}/logs` | Last 100 invocations |
| `DELETE /functions/{name}` | Delete with schedule and logs |

## Limits

| Bound | Value |
| --- | --- |
| Source | 256 KiB |
| Functions | 32 per database |
| Run time | 2 s, memory 32 MiB |
| Request and response | 1 MiB each |
| SQL | 256 `sql` calls per invocation |
| Logs | 50 lines of 1 KiB per invocation |

</div>
