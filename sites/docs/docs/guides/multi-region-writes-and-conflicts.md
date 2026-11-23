---
title: Multi-region writes and conflicts
---

# Multi-region writes and conflicts

Every region of a database's set accepts `INSERT`, `UPDATE` and `DELETE` and commits them where you called. There is no home region and no writer region, and a committed write is never reverted. See [replication and consistency](/concepts/replication) for the two modes.

## How concurrent fast writes converge

In fast mode (the default), two regions can change the same row before either has seen the other's write. Every region then applies the same rules, so all regions end with identical data whatever order the writes arrive in:

| Concurrent writes | Result in every region |
| --- | --- |
| Different columns of the same row | Both changes survive. |
| The same column | The write with the greater hybrid logical clock (HLC) stamp wins; the other is recorded as a conflict. |
| An insert of the same primary key in two regions | The row exists once; each column keeps the value with the greater stamp. |
| A delete and a concurrent update | The later stamp wins. A delete leaves a stamped tombstone, so an older write cannot bring the row back. |
| New `INTEGER PRIMARY KEY` or `SERIAL` values | Never collide: each region generates ids no other region picks. |

HLC stamps compare first by milliseconds, then by a logical counter, then by node id. Arrival order never decides the winner. A losing column value is not an error: the write that carried it still committed and answered success, and any other columns it changed still apply.

## Writes that must not race

When a lost update is not acceptable, run the write in strong mode. A strong transaction checks every row it read and wrote on two of three regions before it commits, and is refused with `409 serialization_conflict` (SQLSTATE `40001`) instead of silently losing to another write. Retry the whole transaction.

```sql
BEGIN STRONG;
SELECT quantity FROM stock WHERE sku = 'sku_1';
UPDATE stock SET quantity = quantity - 1 WHERE sku = 'sku_1' AND quantity > 0;
COMMIT;
```

Schema changes and writes to tables with a `UNIQUE` constraint other than the primary key always run strong, so a unique email or slug cannot be claimed twice from two regions. Strong transactions never produce conflict records.

## Inspect a conflict

Open a database in the [dashboard](https://dash.terrascale.tech/databases) and select **Conflicts**. Filter by the exact table name and a UTC time window. **Since** includes its timestamp; **Until** excludes it. Results are newest first, with **Older conflicts** for the next page.

The conflict log records decisions when they are applied. Reading the application's row later cannot reconstruct the historical winning value: another write may already have changed it.

## Read conflicts with SQL or an SDK

`terrascale.conflicts` is a read-only SQL table in each database. Use the SDK `query` method, SQL over HTTP, WebSocket, or psql:

```sql
SELECT id, kind, table_name, row_key, column_name,
       winner_value, winner_region, winner_hlc, winner_transaction_id,
       loser_value, loser_region, loser_hlc, loser_transaction_id, created_at
FROM terrascale.conflicts
WHERE table_name = 'orders'
  AND julianday(created_at) >= julianday('2026-11-04T12:00:00Z')
  AND julianday(created_at) < julianday('2026-11-05T00:00:00Z')
ORDER BY id DESC
LIMIT 50;
```

Bind the table name and timestamps as query parameters in application code. For the next page, add `AND id < ?` with the last returned id. Use `SELECT *` to include all available conflict metadata.

| Column | Meaning |
| --- | --- |
| `id` | Conflict record ULID; order by this for pagination. |
| `kind` | Conflict reason, such as `superseded`. |
| `table_name`, `row_key` | Affected table and JSON primary key. |
| `column_name` | Losing column; null for a whole-row decision such as a delete. |
| `winner_value`, `loser_value` | JSON of the kept and losing values. |
| `winner_region`, `loser_region` | Regions that committed the writes. |
| `winner_node`, `loser_node` | Nodes that stamped the writes. |
| `winner_hlc`, `loser_hlc` | Commit stamps in HLC order: hex milliseconds, logical counter, then node id. |
| `winner_transaction_id`, `loser_transaction_id` | Transaction ids for both writes. |
| `winner_state`, `loser_state` | `confirmed` and `superseded`: both transactions committed; only the losing column value was replaced. |
| `message` | Reason for the decision. |
| `created_at` | UTC time when the conflict was recorded, distinct from either write's HLC. |

For example, `019a50c1e2f3.00000001:gru-1` beats `019a50c1e2f3.00000000:sjc-1` because its logical counter is greater. The node id only breaks a tie when both the milliseconds and logical counter match.

SQL NULL means no value was recorded (for example, a deleted row); the text `null` is a recorded JSON null value. Values larger than 4 KiB of JSON are represented by `{"sha256":"…","bytes":12345}`.

Each region keeps at most the newest 10,000 conflict records and trims records older than 30 days. Every region records the same decisions once it has applied both writes. Reads use the database's normal read permission; writes to the conflict table are refused.

## Read conflicts with MCP

Call `list_conflicts` on the [dashboard MCP server](/guides/mcp):

```json
{
  "databaseId": "YOUR_DATABASE_ID",
  "table": "orders",
  "since": "2026-11-04T12:00:00Z",
  "until": "2026-11-05T00:00:00Z",
  "limit": 50
}
```

The result contains `conflicts`, `nextBefore`, `hasMore`, and `limit`. Each conflict includes `winner` and `loser` objects with value, region, node, HLC, transaction id and `state`. Pass `nextBefore` as `before`, keeping the filters, to continue. The limit is 1–100; the default is 50. Omit filters to read recent conflicts across tables. `organizationId` and `projectId` follow the MCP server's usual scope selection and permissions.
