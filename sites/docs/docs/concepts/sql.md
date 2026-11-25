---
title: SQL
---

# SQL

Each TerraScale database is a SQL database in the SQLite dialect. It lives in a set of regions you choose, at least one of São Paulo (`gru`), the San Francisco Bay Area (`sjc`) and Frankfurt (`fra`). Every region of the set is an equal peer that commits reads and writes where you called; there is no home region. Writes run in fast mode by default (committed locally in about a millisecond and replicated to the other regions) or in strong mode (serializable across regions on two of three regions). Every response carries a `TerraScale-Bookmark` that gives read-your-writes in any region. See [regions](/concepts/regions) and [replication and consistency](/concepts/replication).

## Quickstart

You need a database named `app` and an API key with read and write access to it. The [getting started guide](/getting-started/) creates both.

### CLI

[Install the CLI](/cli), then:

```sh
ts auth login
ts sql app "CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)"
ts sql app "INSERT INTO customers (email, seats) VALUES ('team@example.com', 12)"
ts sql app "SELECT email, seats FROM customers WHERE seats >= 10"
```

`ts sql` runs one statement in its own transaction and prints rows as JSON lines. With several projects, put `-project <name>` before `sql`.

### psql

The API key is the password; the database name in the URL selects the database. With several projects, prefix the database name with the project and a slash (`{project}/{database}`). A project key reaches its own project; an organization key reaches every project of its organization, and the project in the database name is the one it acts in.

```sh
read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
psql "postgresql://app@pg.terrascale.tech:5432/app?sslmode=verify-full&sslrootcert=system"
```

```sql
SELECT email, seats FROM customers WHERE seats >= 10;
```

Any Postgres driver connects the same way. The Postgres wire also accepts the common Postgres syntax drivers and ORMs send: `$1` parameters, `::type` casts, `SERIAL`, `ILIKE`, `RETURNING`, `ON CONFLICT`, `NOW()` and the `pg_catalog` and `information_schema` views. See [Postgres clients](/guides/postgres-clients).

### HTTP

Exchange the API key for an access token as in [getting started](/getting-started/#exchange-the-key-for-an-access-token), then send statements to `sql/execute`. Use your project ID from the dashboard or `ts projects`.

```bash
curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' \
  -d '{"statements":[{"sql":"SELECT email, seats FROM customers WHERE seats >= ?","args":[10]}]}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

```json
{"results":[{"columns":[{"name":"email","decltype":"TEXT"},{"name":"seats","decltype":"INTEGER"}],"rows":[["team@example.com",12]],"rows_affected":0,"last_insert_rowid":null}]}
```

The C#, TypeScript, Go, Python and Rust SDKs call the same endpoint; see [SDKs](/guides/sdks).

## Query from your application

<SdkQueryTabs />

## What SQL supports

- Tables, indexes (including partial and expression indexes), views and triggers.
- Foreign keys are always enforced, `ON DELETE CASCADE` included.
- JSON is stored as `TEXT`; `json_extract`, `->`, `->>` and `json_each` work on it.
- Values are null, 64-bit integers, doubles, text and blobs. Over HTTP a blob is `{"base64":"…"}`; over the Postgres wire it is `bytea`. A `uuid` column holds the canonical lowercase hyphenated text and binds in text or binary.

## Transactions

- HTTP: each request is one transaction of 1 to 256 statements, all or nothing. `BEGIN STRONG` (or `BEGIN ISOLATION LEVEL SERIALIZABLE`) and `COMMIT` may wrap the whole batch to run it in strong mode; any other `BEGIN`, `COMMIT` or `ROLLBACK` is refused.
- WebSocket: `GET /v1/projects/{project}/databases/{database}/sql/ws` with subprotocol `terrascale.sql.v2`. Send the access token as an `Authorization` header or, from a browser, as a first `{"type":"auth","token":"…"}` frame. `execute` frames run statements as over HTTP; `begin`, `commit` and `rollback` frames hold a transaction of at most 5 seconds. A `begin` frame takes `consistency` (`fast` or `strong`) and `bookmark`, and result frames return the new `bookmark`.
- Postgres wire: `BEGIN`, `COMMIT`, `ROLLBACK` and savepoints work; `BEGIN ISOLATION LEVEL SERIALIZABLE` or `BEGIN STRONG` opens a strong transaction. A transaction lives at most 5 seconds, then rolls back.
- Without a transaction, each statement commits on its own.
- Schema changes, and writes to tables with a `UNIQUE` constraint other than the primary key, always run in strong mode. See [replication and consistency](/concepts/replication).

## Limits

| Limit | Value |
| --- | --- |
| Statement | 16 KiB |
| HTTP request body | 1 MiB |
| Result | 10,000 rows and 8 MiB |
| Rows changed per transaction | 1,024 |
| File growth per transaction | 8 MiB |

## Refused statements

`ATTACH`, `DETACH`, `VACUUM`, `ANALYZE`, temporary and virtual tables, and internal table names are refused, and `FULL JOIN`/`FULL OUTER JOIN` answers `0A000` (`sql_error`) with `full outer join is not supported; use LEFT JOIN with UNION ALL`. `PRAGMA` is limited to `table_info`, `table_xinfo`, `table_list`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, `foreign_key_check` and `foreign_keys`. `sqlite_schema` is readable.

## Errors

Errors use TerraScale codes and PostgreSQL SQLSTATEs across REST, WebSocket and the Postgres wire. For example, `unique_violation` carries `23505`, `foreign_key_violation` carries `23503`, and a retryable `serialization_conflict` carries `40001`. A refused HTTP statement rolls back the request and returns an `error` with `code`, `message`, `statement` and `sqlstate`. Writes with a read-only key answer `403 forbidden_scope` over HTTP and `42501` over the Postgres wire.

Reads need the `sql:read` scope; writes and schema changes also need `sql:write`. A **Read and write** key from the dashboard has both.

## Live shapes and rules

Live shapes send a table snapshot followed by committed changes. Use React's `useQuery` or Svelte's `createQuery`
to read data that changes. These framework examples require the next JavaScript SDK release.
Select rows with `where` and columns with `columns`; rows enter or leave the shape as they start or stop matching.
A `where` clause may use the table's columns, literals, operators, `CASE`, `CAST`, `COLLATE` and the functions `abs`, `coalesce`,
`ifnull`, `iif`, `nullif`, `length`, `octet_length`, `lower`, `upper`, `trim`, `ltrim`, `rtrim`, `substr`, `substring`, `instr`, `round`,
`ceil`, `ceiling`, `floor`, `sign`, `typeof`, `min`, `max`, `likely`, `unlikely`, `likelihood`, `like`, `glob`, `date`, `time`, `datetime`,
`julianday`, `unixepoch`, `strftime`, `json_extract`, `json_type`, `json_valid` and `json_array_length`; anything else answers 400 `unsafe_where`.

```jsx
import { useQuery } from "@terrascale/sdk/react";

function Todos({ db, where = "done = 0" }) {
  const { data, status, error } = useQuery(db.shape("todos", {
    where, columns: ["id", "title"],
  }));
  if (status === "error") return <p role="alert">{String(error)}</p>;
  return <ul aria-busy={status === "loading"}>
    {data.map(todo => <li key={String(todo.id)}>{String(todo.title)}</li>)}
  </ul>;
}
```

```svelte
<script>
  import { createQuery } from "@terrascale/sdk/svelte";

  let { db, where = "done = 0" } = $props();
  const todos = createQuery(db.shape("todos", { where, columns: ["id", "title"] }));
  $effect(() => todos.setShape(db.shape("todos", { where, columns: ["id", "title"] })));
</script>

<ul aria-busy={$todos.status === "loading"}>
  {#each $todos.data as todo (todo.id)}
    <li>{todo.title}</li>
  {/each}
</ul>
```

`data` is the live row array in stable row-key order. Status is `loading` until the snapshot arrives, `live` at the
current head, `reconnecting` while resuming a lost connection, or `error` with a terminal error. A server
`must_refetch` returns to `loading` while replacing the snapshot. Previous rows remain visible while React
switches filters or Svelte's `setShape` switches the store's source. A shape nobody reads for a day is retired;
the next read starts it under a new handle, and a client still holding the old handle gets `must_refetch`.

Equivalent shapes on one client share one stream. It closes two seconds after the last consumer unmounts;
a quick remount reuses it. No application refresh callback is needed.
For code outside a framework, use `db.shape("todos", { where: "done = 0" }).subscribe(({ rows }) => render(rows))`
and call the returned subscription's `close()` when the view closes.

End-user tokens read live shapes and rows as the database's rules allow: a table's `read` rule picks the rows and its
`columns` the columns. A filter limits the shape result; authorization comes from the rules, not the filter. Keep privileged API keys on your server. See the
[JavaScript SDK](https://github.com/TerraScale/terrascale/tree/main/sdk/js#live-sql-table-shapes).

## Agent databases

Create databases with a TTL, fork an existing database or a point in time, and issue a key scoped to one database
with read or read-write access. The database is deleted on expiry. See [agent databases](/guides/agent-databases).

## Recovery and deletion

Point-in-time recovery restores into a new database within the source's recovery window and needs a free database slot. A rewind restores an earlier state in place: one transaction replaces every table, index, view and trigger, keeping the database's id, name and credentials and discarding later changes; see [backups and restore](/guides/backups-and-restore). Database deletion destroys backup data keys, making retained encrypted backups unreadable;
physical cleanup is separate. This does not apply to deleting individual SQL rows. Independent forks remain available.

## Usage caps

Plans enforce a monthly write cap and SQL read fair use. Reaching the write cap stops new writes; reaching SQL read
fair use stops SQL reads, while shape reads stay available. Customer-set hard dollar spending caps are coming.
See [pricing](/pricing).
