# Home apply (PIVOT R6)

A database's home applies transactions that committed in another region (D4, D5). This is TerraBase's own apply:
the engine's push and its server-side apply are never used (D14). R7 (streaming push over JetStream) delivers transactions
to this API; until R7 lands, the internal HTTP endpoint below is the only producer.

## Guarantees

- **All or nothing per transaction.** Every change of a transaction is applied in one write transaction at the home,
  or none is.
- **A rejected transaction never blocks the stream.** A transaction the home cannot apply is rolled back, recorded as
  `reverted` with its changes in `terrascale.conflicts`, and the call succeeds. Only an unavailable home (busy,
  storage fault, shutting down) answers an error, and the same transaction can be sent again.
- **Exactly once.** The transaction id is the dedupe key. Sending an applied or reverted transaction again returns its
  recorded state and changes nothing.
- **Column conflict resolution by HLC (D21).** Each column of each row keeps the stamp of its last write: the writer's
  [`HlcTimestamp`](hlc-clock.md), ordered by milliseconds, logical counter, then node id (ordinal) as the tie-breaker. A change to a column applies only when its stamp is greater.
  A losing column is not applied and is recorded in `terrascale.conflicts` as `superseded`, with its value.
- **Delete tombstones.** A delete keeps a tombstone with its stamp, so an older insert or update of that row cannot
  bring it back (`superseded`). A delete older than a column write of the row loses (`superseded`), and the row stays.
- **Inserts never overwrite.** An insert of a key that exists at the home is a `primary_key` conflict and reverts its
  transaction (D12: tables that take local writes use ULID keys). An update of a row that the home never had reverts
  as `missing_row`.
- **Clock guard.** A transaction stamped more than 60 seconds ahead of the home's clock reverts as `clock` and never
  advances the home's HLC (D21; R12 adds the per-node skew guard).
- **Triggers do not fire for applied changes.** The origin region already ran them and their writes are part of the
  transaction. See [triggers](#triggers).
- **Foreign keys are re-checked at the end of the transaction.** Foreign-key actions (`CASCADE`, `SET NULL`) do not run
  for applied changes; instead every applied child row must reference an existing parent and no applied delete or key
  change may leave a referencing row behind. A violation reverts the transaction (`foreign_key`).
- **Home writes take part.** A write that commits at the home (home-region clients, forwarded transactions) stamps the
  columns it changed with the home's HLC, so a staler change from another region cannot overwrite it.

## Write states

| State | Meaning |
| --- | --- |
| `provisional` | Committed in its region; the home has not applied it yet. The home answers this for an unknown id. |
| `confirmed` | Applied at the home. Some columns may have lost to newer writes (`superseded` rows in `terrascale.conflicts`). |
| `reverted` | Rejected at the home; none of its changes are applied. The origin region must revert its rows to the home's images. |

## C# API (in-process, for R7)

`IHomeApply` in `TerraBase.Features.HomeApply`:

```csharp
ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken);
ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken);
```

`ApplyAsync` runs on the database's home only, inside the organization's QoS context (E11). It throws only when the
home is unavailable (`StorageBusyException`, a busy `SqlStatementException`, cancellation, shutdown) and returns null
when the database is not stored on this node; every data outcome is a result. Validate the transaction as
`ApplyEndpoint.Invalid` does before calling it. Callers ack a
JetStream message after `ApplyAsync` returns, whatever its state.

## Internal HTTP

Internal listener only, behind the peer credential; 404 on the public listener.

- `POST /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/apply`: body is one
  `RegionTransaction`, at most 8 MiB, sent with `Content-Length`; answer `200` with a `HomeApplyResult`. `404` when the database is not homed
  here or its placement does not name the origin region; `400` for a malformed transaction; `503` when the home is
  unavailable (retry).
- `GET …/databases/{database}/apply/{transaction}`: the transaction's `HomeApplyResult`, or
  `{"id":…,"state":"provisional"}` when the home has not seen it (or applied it more than 7 days ago).

### RegionTransaction

```json
{
  "id": "01JC3Y6Q4M2V8K5T7W9X1Z3B5D",
  "region": "sjc",
  "hlc": { "physicalMilliseconds": 1793805256123, "logical": 0, "nodeId": "sjc-1" },
  "changes": [
    { "op": "insert", "table": "users", "values": { "id": "01JC…", "email": "a@example.com", "credits": 5 } },
    { "op": "update", "table": "users", "key": { "id": "01JC…" }, "values": { "credits": 7 } },
    { "op": "delete", "table": "sessions", "key": { "id": "01JC…" } }
  ]
}
```

- `id`: a ULID minted by the origin region for the transaction.
- `region`: the origin region; it must be in the database's placement and differ from the home.
- `hlc`: the transaction's commit `HlcTimestamp` on the origin node (R12, [hlc-clock.md](hlc-clock.md)). Every change of
  the transaction carries this stamp; a replay keeps it.
- `changes`: 1 to 1024, in commit order. `table` is a table of the database with a declared `PRIMARY KEY` (D12: ULID
  keys); `key` names every primary-key column; `insert` carries every column of the new row, `update` only the
  columns it changed (never a key column), `delete` only the key.
- Values are SQL values as in `/sql/execute`: `null`, an integer, a number with a fraction or exponent for a real,
  a string, or `{"base64": …}` for a blob. Send integral reals with a fraction (`2.0`).

### HomeApplyResult

```json
{
  "id": "01JC3Y6Q4M2V8K5T7W9X1Z3B5D",
  "state": "reverted",
  "conflicts": [
    { "kind": "unique", "table": "users", "key": { "id": "01JC…" }, "message": "UNIQUE constraint failed: users.email" }
  ],
  "rows": [
    { "table": "users", "key": { "id": "01JC…" }, "values": null }
  ]
}
```

- `state`: `confirmed` or `reverted` (or `provisional` from the state read).
- `conflicts`: what `terrascale.conflicts` recorded for this transaction.
- `rows`: for `reverted` only, the home's current image of every row the transaction touched (`values: null` when the
  row does not exist at the home), so the origin region and its shape overlay can revert to it (D9 step 3).

## `terrascale.conflicts`

Every database has a read-only `terrascale.conflicts` table, readable with SQL on every surface (REST, WebSocket,
Postgres wire) in every region. One record per losing column of a `superseded` change, one per row for a row-level
loss (a delete, or a write against a newer delete), and one per `reverted` transaction. The winner is the write the
home kept; the loser is the region transaction that lost.

| Column | Type | Meaning |
| --- | --- | --- |
| `id` | TEXT | ULID of the record, in record order. |
| `kind` | TEXT | `superseded`, `unique`, `primary_key`, `not_null`, `check`, `foreign_key`, `constraint`, `missing_row`, `schema`, `clock`. |
| `table_name` | TEXT | The table, or null when the rejection is not about one row. |
| `row_key` | TEXT | JSON object of the row's primary key, or null. |
| `column_name` | TEXT | The losing column, or null for a row-level loss and for `reverted`. |
| `winner_value` | TEXT | JSON of the value the home kept: the column's value, the row (`{"column": value, …}`), or null when the row is deleted or unknown. |
| `loser_value` | TEXT | JSON of the losing value: the column's value, the row's changed values, null for a losing delete, or the whole transaction's changes for `reverted`. |
| `winner_region` | TEXT | Region of the kept write, or null when no write of the row is recorded. |
| `winner_node` | TEXT | Node that stamped the kept write. |
| `winner_hlc` | TEXT | HLC of the kept write. |
| `winner_transaction_id` | TEXT | ULID of the kept write's transaction (a region transaction, or a home transaction's id). |
| `winner_state` | TEXT | `confirmed`, or null with no winner. |
| `loser_region` | TEXT | Region the losing transaction committed in. |
| `loser_node` | TEXT | Node that stamped it. |
| `loser_hlc` | TEXT | Its commit HLC. |
| `loser_transaction_id` | TEXT | Its id. |
| `loser_state` | TEXT | `superseded` (the transaction applied, this part lost) or `reverted` (nothing applied). |
| `message` | TEXT | Why, without engine names. |
| `created_at` | TEXT | When the home recorded it (UTC, ISO 8601). |

- **Values.** A value whose JSON is longer than 4 KiB is recorded as `{"sha256": "<hex of its JSON>", "bytes": <length>}`
  instead, so `terrascale.conflicts` never holds a copy of a large value.
- **HLCs** are fixed width and sort in HLC order: 12 hex digits of Unix milliseconds, `.`, 8 hex digits of the logical
  counter, `:`, the node id (`019a50c1e2f3.00000000:sjc-1`).
- **Retention.** The home keeps at most the newest 10,000 records per database, none older than 30 days; each
  transaction that records a conflict trims the rest.

```sql
SELECT loser_transaction_id, kind, table_name, row_key, column_name, winner_value, loser_value
FROM terrascale.conflicts WHERE loser_state = 'reverted' ORDER BY id DESC LIMIT 20;
```

Writes to it are refused with `sql_sandbox`.

## Triggers

A trigger fires once, where its write commits. Every `CREATE TRIGGER` runs with an extra condition that is true for
every customer statement and false while the home applies a region transaction, so applied rows (which already hold
the trigger's own writes from the origin) do not fire it again. Schema reads (`sqlite_schema`) show the trigger as it
was written.

## Storage

Per database, internal and invisible to customers, created when the database is created with replica regions, gains
them, or first applies a transaction: `_ts_rows` (per column and per tombstone: the write's HLC, region and transaction id), `_ts_applied` (each applied or
reverted transaction with its result, kept 7 days, so a transaction is exactly once within that window) and
`_ts_conflicts`. A database without them stamps nothing and reads an empty `terrascale.conflicts`. Rows written at the
home before the database had replica regions carry no stamp and lose to any region write, which can only be newer.
The tables are created in place with no format change: older files gain them on first use.

## Known gaps

- Tombstones in `_ts_rows` are kept until the row's key is written again.
- Applied writes are metered where they committed (the origin region), not again at the home.
