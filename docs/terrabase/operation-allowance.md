# Monthly operation allowance, write cap and SQL read fair use

Every plan includes monthly write units ([pricing.json](https://terrascale.tech/pricing.json),
`metering.allowances`). Free (hobby) stops new writes at its allowance. Paid plans keep writing past
it, ts-dashboard bills the units over it on the Stripe subscription invoice, and new writes stop at
the plan's hard cap (`limits.operationUnitCapPerMonth`). SQL reads are never billed; they stop at
the plan's fair use (`limits.sqlReadUnitsPerMonth`). Shape reads are free and never counted.
Enterprise has no monthly limit.

| Plan | Write units included | Write cap | SQL read fair use |
|---|---|---|---|
| hobby | 50,000 | 50,000 | 500,000 |
| starter | 250,000 | 2,500,000 | 2,500,000 |
| pro | 2,000,000 | 20,000,000 | 20,000,000 |
| team | 5,000,000 | 50,000,000 | 50,000,000 |
| enterprise | 10,000,000 | none | none |

The values are data in the published catalog, `landing-site/static/pricing.json` (`limits.operationUnitsPerMonth`,
`operationUnitCapPerMonth` and `sqlReadUnitsPerMonth`); TerraBase embeds that file and reads its caps
from it (`Features/Qos/PlanPricing.cs`).

## Units

One unit is each started 4 KiB (4,096 bytes), `OperationUnits.Of`.

- A committed SQL write bills the larger of its changed rows and the started 4 KiB the transaction
  grew the database by. DDL that changes no rows, rolled-back and failed transactions, and refused
  statements bill nothing.
- A SQL read of tables counts each returned row's started 4 KiB (text as UTF-8, 8 bytes per number,
  at least one unit) toward fair use. Catalog and constant reads count nothing.
- Shape reads (`…/shape`, CDN chunks) count nothing.
- Replication bills nothing, so a write counts once, at the database's home.

## One month, every region

Each node meters only the work it serves, once: a write at the database's home (also when another region forwarded
the request or relayed the session), a replica read in the replica region. It adds those units to its own records
`yyyy-MM~node` of `projects/{project}/usage-months` and `organizations/{organization}/usage-months` in its system
database, in the usage flush (every 10 s). No other region holds those records, so every total sums the regions:

- **Project usage** (`GET …/projects/{project}/usage`): the answering node sends the same client request, with the
  peer credential and `X-TerraBase-Usage-Scope: node`, to every node of the other regions (`TS_PEERS_FILE`). Each
  answers its own share (its databases, its units of the project; `404` when it does not hold the project), and the
  organization's units come from `GET /v1/internal/usage/months` of every region. A region that cannot answer within
  3 seconds makes the request answer `503`, never a smaller count.
- **Allowance page** (`GET /v1/usage/allowances`): every region answers its own page; the merged page ends at the
  smallest last organization of a region with more to page, so every listed organization sums every region.
- **Plan limits:** a node adds every other region's units of the organization, asked on its first check and on every
  usage flush tick, to its own. A region that does not answer keeps its last answer until the next tick.

A month reads every node's own record plus its unflushed units. Units of databases deleted since stay in the month.

`GET /v1/projects/{project}/usage` returns `month`:

| Field | Meaning |
|---|---|
| `month` | The UTC month, `yyyy-MM` |
| `readUnits`, `writeUnits` | The project's SQL read and write units this month |
| `units` | The project's billed write units |
| `organizationUnits` | Billed write units of every project: what the allowance and cap count |
| `organizationReadUnits` | SQL read units of every project: what fair use counts |

### Every organization, for the dashboard

`GET /v1/usage/allowances[?after=<organization id>][&month=<complete yyyy-MM>]` on the internal
listener (shared peer credential) pages every organization, 25 at a time in id order:
`periodStart`, `observedAt`, `items[{organizationId, operations, logicalBytes, sqlReads, storageByteHours}]`,
`next`, `operationsContract` (`write-units-4kib-global-v2`) and `storageContract`
(`stored-byte-hours-global-v1`). `operations` is the billed write units; `sqlReads` the fair-use read
units; `storageByteHours` the stored bytes times hours this month. Once an hour each node measures the
stored bytes of every organization's databases it holds and adds bytes × hours since its last sample
(one hour for its first sample of the month) to its own month record, so a restart in the same hour
adds nothing. ts-dashboard bills the month's average stored bytes over the plan allowance per
GB-month, in MB-months. Omitting `month` returns the current UTC month. An explicit `month` accepts any
complete calendar month in `yyyy-MM` form; the current month, future months and malformed values
return `400 invalid_request` in the existing error envelope. A month becomes complete at the first
instant of the next month (00:00 UTC) on the service clock.

For an explicit month, `operations=null`, `sqlReads=null` and `storageByteHours=null` mean that no
persisted usage-month record and no unflushed unit exists for that organization on any answering
node. This is unavailable history, never zero usage. A persisted record containing zero totals returns
numeric zeros. A node without records contributes nothing when another node has records for the
organization. `logicalBytes` remains null. Units a node has not flushed to its record yet are counted,
as they are for the current month. Paging, ordering and the JSON fields are unchanged.

Responses expose `Usage-Final: false`. Month completeness is a calendar boundary, not an accounting
watermark: a buffered or uncertain flush can still persist units attributed to a closed month. There
is no enforced maximum delay, so no finite waiting interval guarantees final totals. Operations are
attributed when recorded; an operation completing in the next month is counted there. Storage samples
are attributed to their sampling hour. A scan that crosses into another UTC month or runs past 30
seconds answers `503`. Responses carry `Cache-Control: no-store`.

Usage-month records have no time-based expiry or pruning in the service. The separate minute, hour
and day chart buckets do expire; that pruning does not remove usage-month records. Project deletion
removes project-scoped records, including project usage months, while organization totals survive.
Organization deletion removes the organization from the catalog used by this page, so it can disappear
between page requests even though its month records are not age-pruned. Pages are live reads, not a
snapshot across requests; creation, deletion and delayed flushes can change what later pages observe.

## Behavior

- A write statement is refused once the organization's write units this month reach the cap (the
  allowance on hobby): HTTP `429 quota_exceeded` with SQLSTATE `53100` on REST, the WebSocket and
  the Postgres wire, and the upgrade path. Nothing in the statement runs.
- A SQL read of tables is refused the same way once fair use is reached. Shape reads keep working.
- The month resets at 00:00 UTC on the first day of the next calendar month.
- A node reads the organization's month on its first check of the month, then on every usage flush
  tick, adding the units it serves in between.
- A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
