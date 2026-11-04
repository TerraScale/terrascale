---
title: Multi-region writes and conflicts
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Multi-region writes and conflicts

When writes from different regions reach the same row and column, the database's home compares their hybrid logical clock (HLC) stamps. The greater stamp wins for that column: first milliseconds, then the logical counter, then the node ID in ordinal order. Writes to different columns can both survive. Arrival order does not decide the winner.

This is the home-apply rule. Regional write acceptance and delivery depend on the connection and deployed data plane; see [replication](/de/concepts/replication) for the current connection behavior. A conflict record does not make a provisional write confirmed.

## Inspect a conflict

Open a database in the [dashboard](https://dash.terrascale.tech/databases) and select **Conflicts**. Filter by the exact table name and a UTC time window. **Since** includes its timestamp; **Until** excludes it. Results are newest first, with **Older conflicts** for the next page. Expand a conflict's reason to see why the write lost.

The conflict log records decisions at apply time. Reading the application's row later cannot reconstruct the historical winning value: another write may already have changed it.

## Transaction states

| Transaction state | Meaning |
| --- | --- |
| `provisional` | Committed in its origin region; the home has not applied it yet. |
| `confirmed` | Applied at the home. Individual columns can still lose to greater HLC stamps. |
| `reverted` | Rejected at the home. None of the transaction's changes applied. |

A conflict's `state` is `superseded` or `reverted`. `superseded` records losing columns in a **confirmed** transaction. A provisional transaction has no conflict record until the home makes its decision. An empty conflict list does not prove that a transaction was confirmed.

A delete leaves a stamped tombstone, so an older write cannot resurrect the row. Inserts with an existing primary key and updates to a missing row revert the transaction. Unique, foreign-key and other constraint failures also revert the whole transaction. A stamp more than 60 seconds ahead of the home clock is rejected as `clock`.

## Read conflicts with SQL or an SDK

`terrascale.conflicts` is a read-only SQL table in each database. Use the existing SDK `query` method, SQL over HTTP, WebSocket, or psql:

```sql
SELECT id, transaction_id, region, state, kind,
       table_name, row_key, change, message, created_at
FROM terrascale.conflicts
WHERE table_name = 'orders'
  AND julianday(created_at) >= julianday('2026-11-04T12:00:00Z')
  AND julianday(created_at) < julianday('2026-11-05T00:00:00Z')
ORDER BY id DESC
LIMIT 50;
```

Bind the table name and timestamps as query parameters in application code. For the next page, add `AND id < ?` with the last returned ID. Use `SELECT *` to include all available conflict metadata.

| Column | Meaning |
| --- | --- |
| `id` | Conflict record ULID; order by this for pagination. |
| `transaction_id` | Origin transaction ULID. |
| `region` | Origin region of the losing write. |
| `state` | `superseded` or `reverted`. |
| `kind` | Conflict reason, such as `superseded`, `primary_key`, `foreign_key`, or `clock`. |
| `table_name`, `row_key` | Affected table and JSON primary key, when the rejection identifies a row. |
| `change` | JSON losing columns for `superseded`; the transaction's changes for `reverted`. |
| `message` | Reason for the decision. |
| `created_at` | UTC time when the home recorded the conflict, distinct from the write's HLC. |

The database keeps its newest 10,000 conflict records. Replicas may show the log after replication catches up. The dashboard reads at the home. Reads use the database's normal read permission; writes to the conflict table are refused.

## Read conflicts with MCP

Call `list_conflicts` on the [dashboard MCP server](/de/guides/mcp):

```json
{
  "databaseId": "YOUR_DATABASE_ID",
  "table": "orders",
  "since": "2026-11-04T12:00:00Z",
  "until": "2026-11-05T00:00:00Z",
  "limit": 50
}
```

The result contains `conflicts`, `nextBefore`, `hasMore`, and `limit`. Each conflict includes `transactionState` alongside its conflict `state`. Pass `nextBefore` as `before`, keeping the filters, to continue. The limit is 1–100; the default is 50. Omit filters to read recent conflicts across tables. `organizationId` and `projectId` follow the MCP server's usual scope selection and permissions.

</div>
