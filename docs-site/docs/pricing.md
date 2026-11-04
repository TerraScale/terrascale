# Pricing

Prices and limits come from the [published catalog](https://terrascale.tech/pricing.json).

| Plan | Monthly price (USD) | Included write units | Write cap | SQL read fair use | Storage |
| --- | --- | --- | --- | --- | --- |
| Free | $0 | 50,000 | 50,000 | 500,000 | 0.1 GB |
| Starter | $3 | 250,000 | 2,500,000 | 2,500,000 | 1 GB |
| Pro | $20 | 2,000,000 | 20,000,000 | 20,000,000 | 10 GB |
| Team | $50 | 5,000,000 | 50,000,000 | 50,000,000 | 25 GB |
| Enterprise | $999 | 10,000,000 | No limit | No limit | 100 GB |

## What is billed

- **Writes.** One operation unit is each started 4 KiB (4,096 bytes) of data successfully written; every changed row counts at least one unit. Only writes are billed.
- **Storage.** Stored bytes in the database's home region, sampled hourly. One GB-month is 1 GB (10^9 bytes) stored for a whole month.
- **Reads.** SQL reads are not billed; they count toward the plan's monthly fair use. Shape reads served from the edge are free and not counted.

Free includes small allowances without a card; new writes stop at its operation allowance and there are no overage charges. Paid plans include monthly allowances; operation units over the allowance are added to the next subscription invoice at the overage rate, up to the plan's hard cap (operationUnitCapPerMonth), where new writes stop until the next UTC month or an upgrade. SQL reads stop at the plan's fair use (sqlReadUnitsPerMonth); shape reads stay available. Storage over the allowance is added to the next invoice at the storage overage rate per GB-month of average stored bytes above it. Client egress over the allowance is not billed yet.

Overage rates on paid plans: $5 per million write units and $0.5 per GB-month of storage.

When a plan's write cap or SQL read fair use is reached, the statement answers `429 quota_exceeded` (Postgres SQLSTATE `53400`) until the next UTC month or an upgrade.

See [support](/legal/support) for support targets.
