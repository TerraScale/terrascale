---
title: Replication and consistency
---

# Replication and consistency

A database lives in a set of regions you choose, at least one. Every region of the set is an equal peer: each one accepts reads, `INSERT`, `UPDATE` and `DELETE`, and commits them where you called. There is no home region, no writer region and no forwarding to another region. A committed write is never reverted. See [regions](/concepts/regions) to choose the set.

Each request runs in one of two modes, chosen with the `TerraScale-Consistency` header:

| Mode | How a write commits | Typical commit latency | Use it for |
| --- | --- | --- | --- |
| `fast` (default) | In the region you called, on two of the three nodes in that region. Peer regions receive it right after. | about 1–2 ms | Almost every write |
| `strong` | On two of the three regions (the region you called plus the closest peer), after checking everything the transaction read and wrote. | one round trip to the closest peer region | Balances, inventory, single-use tokens, anything that must never double-spend |

A database also has a default mode, `defaultConsistency`, used when a request names none. It is `fast` unless you set it when you create or update the database.

## Fast mode

Fast mode is the default. A write commits in the region you called, is durable on two of the three nodes in that region before it is acknowledged, and then reaches every other region of the set over a direct, encrypted connection between regions. In TerraScale's own measurements a fast write becomes visible in the other regions about one fiber round trip later: roughly 90 ms between São Paulo and the San Francisco Bay Area and about 110 ms between São Paulo and Frankfurt. There is no published production replication-lag guarantee.

Fast mode never reverts a write after it answered. When two regions change the same row at nearly the same time, every region converges to the same result:

- **Different columns of the same row** both survive.
- **The same column** keeps the write with the greater hybrid logical clock (HLC) stamp; the other write is recorded as a conflict.
- **A delete** leaves a stamped tombstone, so an older write cannot bring the row back. A later re-insert of the same key does bring it back.
- **`INTEGER PRIMARY KEY`, `SERIAL` and `BIGSERIAL` values** are generated so that regions never pick the same new id.

Every region applies the same rules to the same writes, so each region ends with identical data whatever order the writes arrived in. See [multi-region writes and conflicts](/guides/multi-region-writes-and-conflicts) to inspect conflicts.

## Strong mode

Strong mode gives serializable ACID transactions across regions. The region you called runs the transaction, records exactly which rows it read and which rows it wrote, and asks every peer region to accept it. It commits as soon as one peer accepts, so two of three regions agree, which costs one round trip to the closest peer region. In TerraScale's own measurements that is about 170 to 180 ms between the three launch regions.

A strong transaction is refused, and nothing is applied, if any row it read or wrote changed after it started. Older transactions take priority over younger ones (wound-wait), so contending transactions always make progress instead of retrying each other forever. A refused transaction answers `409 serialization_conflict` with SQLSTATE `40001`; run the whole transaction again.

A request runs strong when any of these is true:

- It sends `TerraScale-Consistency: strong`, or the SDK call passes `consistency: "strong"`.
- It opens a transaction with `BEGIN STRONG` or `BEGIN ISOLATION LEVEL SERIALIZABLE` (on HTTP, around the whole batch), or on the Postgres wire runs `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE`.
- It changes the schema (`CREATE`, `ALTER`, `DROP`). Schema changes always run strong, so every region agrees on the schema.
- It writes to a table with a `UNIQUE` constraint or unique index other than its primary key. Uniqueness across regions needs agreement, so those writes always run strong.
- The database's `defaultConsistency` is `strong` and the request names no mode.

The response header `TerraScale-Consistency` says which mode the request actually ran in.

## Read your writes in any region

Every SQL response carries a `TerraScale-Bookmark` header; WebSocket result frames carry it as `bookmark`. A bookmark is a short opaque string, about 17 bytes for three regions, that says which writes the response has seen. Send the newest bookmark you hold on a later request, in any region, and that region waits until it has applied those writes before it runs the request. You read your own writes everywhere, without sticky routing.

```http
POST /v1/projects/{project}/databases/app/sql/execute
TerraScale-Bookmark: AQMDZnJhBANncnUKA3NqYwc
Content-Type: application/json

{"statements":[{"sql":"SELECT status FROM orders WHERE id = ?","args":["o_42"]}]}
```

The SDKs track bookmarks for you: each client keeps the newest bookmark per database and sends it on the next request, so read-your-writes is automatic. To carry it across processes (for example from an API server to a background job), read it from the client and pass it explicitly:

```js
await db.execute("UPDATE orders SET status = 'paid' WHERE id = ?", ["o_42"]);
await queue.send({ orderId: "o_42", bookmark: db.bookmark });

// In the worker, possibly in another region:
const rows = await db.query("SELECT status FROM orders WHERE id = ?", ["o_42"], { bookmark: job.bookmark });
```

A request without a bookmark reads the state its region already holds, which can briefly lag writes made in other regions. An empty or missing bookmark means "no causal requirement". A malformed bookmark answers `400 invalid_bookmark`.

## Choose a mode per request

::: code-group

```js [JavaScript]
await db.execute("INSERT INTO events (id, kind) VALUES (?, ?)", [id, "signup"]);
await db.execute("UPDATE accounts SET balance = balance - ? WHERE id = ?", [25, account], { consistency: "strong" });
```

```python [Python]
db.execute("INSERT INTO events (id, kind) VALUES (?, ?)", [id, "signup"])
db.execute("UPDATE accounts SET balance = balance - ? WHERE id = ?", [25, account], consistency="strong")
```

```go [Go]
_, err := db.Execute(ctx, "INSERT INTO events (id, kind) VALUES (?, ?)", id, "signup")
_, err = db.Execute(tb.WithConsistency(ctx, tb.ConsistencyStrong), "UPDATE accounts SET balance = balance - ? WHERE id = ?", 25, account)
```

```rust [Rust]
let db = client.database("app");
db.sql().execute("INSERT INTO events (id, kind) VALUES (?, ?)", &[id.as_str().into(), "signup".into()]).await?;
db.with_consistency(ConsistencyMode::Strong).sql()
    .execute("UPDATE accounts SET balance = balance - ? WHERE id = ?", &[25.into(), account.as_str().into()]).await?;
```

```sql [psql]
BEGIN ISOLATION LEVEL SERIALIZABLE;
UPDATE accounts SET balance = balance - 25 WHERE id = 'acct_1';
COMMIT;
```

```http [HTTP]
POST /v1/projects/{project}/databases/app/sql/execute
TerraScale-Consistency: strong
Content-Type: application/json

{"statements":[{"sql":"UPDATE accounts SET balance = balance - ? WHERE id = ?","args":[25,"acct_1"]}]}
```

:::

The CLI takes the same choice: `ts sql app "UPDATE …" -consistency strong`, and `-bookmark <value>` to read after an earlier write. See the [CLI](/cli#run-sql).

## Recovery and availability

Every region of the set keeps serving reads and fast writes on its own. If a region becomes unreachable, the other regions carry on with no failover step and no lost acknowledged write. When the region comes back it catches up from its peers and converges to identical data.

Strong transactions need two of the three regions. With three regions in the set, strong mode keeps working while any one region is down. A database in a single region commits strong transactions in that region alone; a database in two regions needs both for strong mode.

Inside each region, every commit is acknowledged only after two of the region's three nodes hold it, so losing one node loses no acknowledged write.

A copy in another region is not a backup: it follows every change, including mistakes. Use point-in-time recovery to restore an earlier state into a new database, or a rewind to restore it in place. See [backups and restore](/guides/backups-and-restore).

## Encryption and deletion

Database files and the change log in every region of the set are encrypted at rest. Deleting a database destroys its data keys, making retained encrypted backups unreadable. Crypto-shredding and physical removal are separate steps; an individual SQL row deletion does not destroy a database's keys. Independent forks remain available until deleted or expired.

## Lag

`GET /v1/projects/{project}/databases/{database}/regions` and `ts db regions <db>` report, per region, whether it is healthy, its replication lag in milliseconds and the newest bookmark it has applied. There is no published production replication-lag guarantee; the figures on this page come from TerraScale's own measurements between the launch regions.
