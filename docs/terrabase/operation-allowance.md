# Monthly operation allowance

Plans use hard allowances during Public Alpha ([pricing.json](https://terrascale.tech/pricing.json),
`metering.allowances`): new billable work stops at the allowance, and reads, exports, deletion and
usage reduction remain available.

| Plan | Operations per UTC month |
|---|---|
| hobby | 100,000 |
| pro | 100,000,000 |
| enterprise | unlimited |

Values live in `QosPolicy.MonthlyOperations` (`src/TerraBase/Features/Qos/QosPolicy.cs`).

## Behavior

- Every document read and write the usage meter counts (gets, list and query pages, SQL runs,
  puts, patches, deletes) counts toward the organization's month.
- A write request that holds a put or patch is refused once the month reaches the allowance:
  `429` with code `operation_allowance`, a message naming the plan and allowance, and the upgrade
  path (`https://dash.terrascale.tech/organization?tab=billing`). Nothing in the request runs.
- Deletes, assertion-only transactions and reads still succeed. The month resets at 00:00 UTC on
  the first day of the next calendar month.

## Limits of this version

- Counts are per node: each region enforces the allowance on the operations it served.
- One document operation counts as one unit; the 4 KiB operation-unit size is not yet applied.
- After a restart a node seeds each database's month from its saved usage in the background the
  first time it serves the database again (`UsageMeter.MonthOperations`); writes never wait for
  that read, so a few writes can pass before it lands.

Covered by `tests/TerraBase.Fast/Qos/OperationAllowanceTests.cs`.
