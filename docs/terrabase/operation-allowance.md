# Monthly operation allowance

Every plan includes monthly operation units ([pricing.json](https://terrascale.tech/pricing.json),
`metering.allowances`). Only hobby (the Free plan) stops there: new billable work stops at its
allowance, and reads, exports, deletion and usage reduction remain available. Paid plans (starter,
pro, team) keep writing past their allowance; ts-dashboard reads `GET /v1/usage/allowances` and
bills the units over it on the Stripe subscription invoice. Enterprise has no allowance check.

| Plan | Operation units included per UTC month | At the allowance |
|---|---|---|
| hobby | 50,000 | new puts and patches stop |
| starter | 250,000 | billed overage |
| pro | 2,000,000 | billed overage |
| team | 5,000,000 | billed overage |
| enterprise | unlimited | — |

Values live in `QosPolicy.MonthlyOperations` (`src/TerraBase/Features/Qos/QosPolicy.cs`).

## Operation units

One unit is each started 4 KiB (4,096 bytes) of a document successfully read or written
(`metering.operationUnit`, `OperationUnits.Of`):

- A put or patch bills its stored document's units; a delete bills one unit.
- A get bills the document's units; a not-found get bills none. List, query and export pages bill
  each returned document. SQL query runs bill each returned row's value.
- SQL over HTTP and Postgres wire bill the same way: an inserted or updated row bills
  its stored document's units and a deleted row one unit, once its transaction commits; a rolled-back
  or failed transaction bills no writes, and replaying a transaction's statements bills nothing again.
  A read of tables bills each returned row's started 4 KiB (text as UTF-8, 8 bytes per number, at
  least one unit) when it answers, inside a transaction too. Catalog and constant reads, DDL, and the
  rows an `UPDATE`, `DELETE` or `INSERT … SELECT` reads to find its targets bill nothing.
- Refused, failed and invalid writes bill nothing. Replication (peer stream applies, refills,
  anti-entropy) bills nothing (user decision 2026-10-23), so a write counts once, in the region
  that served it.
- Example: 10 writes of 9 KiB documents are 30 units.

## One month, every region

Each node adds the units it served to its own documents `yyyy-MM~node` of the replicated catalog
collections `projects/{project}/usage-months` and `organizations/{organization}/usage-months`, in
the same usage flush (every 10 s) that saves operation counts. A month reads the sum of every
node's document plus this node's unflushed units, so every region answers the same total once the
catalog has replicated. Units of databases deleted since stay in the month.

`GET /v1/management/organizations/{organization}/projects/{project}/usage` returns them in
`month`:

| Field | Meaning |
|---|---|
| `month` | The UTC month, `yyyy-MM` |
| `readUnits`, `writeUnits`, `units` | The project's read, write and total units this month |
| `organizationUnits` | Total units of every project of the organization: what the allowance counts |

### Every organization, for the dashboard

`GET /v1/usage/allowances[?after=<organization id>]` on the internal listener (shared peer
credential, the same identity as `PUT /admin/projects/{project}/plan` and
`PUT /admin/organizations/{organization}/plan`, see [plan-database-allowance.md](plan-database-allowance.md)) pages every organization,
25 at a time in id order: `periodStart`, `observedAt`, `items[{organizationId, operations,
logicalBytes}]`, `next`, `operationsContract` (`operation-units-4kib-global-v1`)
and `storageContract`. `operations` equals `organizationUnits`. Storage is not reported yet:
`logicalBytes` is null and `storageContract` is `none`.
`periodStart` and `observedAt` are taken before the scan; a scan that crosses into another UTC
month or runs past 30 seconds answers `503`, and the caller retries. Responses carry
`Cache-Control: no-store`.

## Behavior

- On hobby, a write request that holds a put or patch is refused once the organization's units this
  month reach the allowance: `429` with code `operation_allowance`, a message naming the plan and
  allowance, and the upgrade path (`https://dash.terrascale.tech/organization?tab=billing`).
  Nothing in the request runs.
- Deletes, assertion-only transactions and reads still succeed. The month resets at 00:00 UTC on
  the first day of the next calendar month.
- A node reads the organization's month on its first write check of the month, then again on
  every usage flush tick, adding the units it serves in between. Units another region served reach
  it within about one flush tick plus catalog replication.

Covered by `tests/TerraBase.Fast/Qos/OperationAllowanceTests.cs`, `tests/TerraBase.Fast/Qos/PlanAllowanceTests.cs`,
`tests/TerraBase.Fast/Replication/FreeReplicationUsageTests.cs` and `tests/TerraBase.Fast/Sql/SqlMeteringTests.cs`.
