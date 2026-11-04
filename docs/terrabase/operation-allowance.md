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
- Replication bills nothing, so a write counts once, in the region that served it.

## One month, every region

Each node adds the units it served to its own documents `yyyy-MM~node` of the replicated catalog
collections `projects/{project}/usage-months` and `organizations/{organization}/usage-months`, in
the usage flush (every 10 s). A month reads the sum of every node's document plus this node's
unflushed units. Units of databases deleted since stay in the month.

`GET /v1/management/organizations/{organization}/projects/{project}/usage` returns `month`:

| Field | Meaning |
|---|---|
| `month` | The UTC month, `yyyy-MM` |
| `readUnits`, `writeUnits` | The project's SQL read and write units this month |
| `units` | The project's billed write units |
| `organizationUnits` | Billed write units of every project: what the allowance and cap count |
| `organizationReadUnits` | SQL read units of every project: what fair use counts |

### Every organization, for the dashboard

`GET /v1/usage/allowances[?after=<organization id>][&month=<previous yyyy-MM>]` on the internal
listener (shared peer credential) pages every organization, 25 at a time in id order:
`periodStart`, `observedAt`, `items[{organizationId, operations, logicalBytes, sqlReads, storageByteHours}]`,
`next`, `operationsContract` (`write-units-4kib-global-v2`) and `storageContract`
(`stored-byte-hours-global-v1`). `operations` is the billed write units; `sqlReads` the fair-use read
units; `storageByteHours` the stored bytes times hours this month. Once an hour each node measures the
stored bytes of every organization's databases it holds and adds bytes × hours since its last sample
(one hour for its first sample of the month) to its own month document, so a restart in the same hour
adds nothing. ts-dashboard bills the month's average stored bytes over the plan allowance per
GB-month, in MB-months. `month` selects the previous UTC month,
so ts-dashboard bills a month's last units during the first three days after it closes. A scan
that crosses into another UTC month or runs past 30 seconds answers `503`. Responses carry
`Cache-Control: no-store`.

## Behavior

- A write statement is refused once the organization's write units this month reach the cap (the
  allowance on hobby): HTTP `429 quota_exceeded` with `sqlite_code` `SQLITE_QUOTA`, Postgres wire
  SQLSTATE `53400`, and the upgrade path. Nothing in the statement runs.
- A SQL read of tables is refused the same way once fair use is reached. Shape reads keep working.
- The month resets at 00:00 UTC on the first day of the next calendar month.
- A node reads the organization's month on its first check of the month, then on every usage flush
  tick, adding the units it serves in between.

Covered by `tests/TerraBase.Fast/Sql/SqlMeteringTests.cs`.
