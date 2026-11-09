---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Pricing

Prices and limits come from the [published catalog](https://terrascale.tech/pricing.json).

| Plan | Monthly price (USD) | Yearly price (USD) | Included operation units | Write cap | SQL read fair use | Client egress | Storage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Free | $0 | $0 | 50,000 | 50,000 | 500,000 | 1 GB/month | 0.1 GB |
| Starter | $3 | $30 | 250,000 | 2,500,000 | 2,500,000 | 5 GB/month | 1 GB |
| Pro | $20 | $200 | 2,000,000 | 20,000,000 | 20,000,000 | 50 GB/month | 10 GB |
| Team | $50 | $500 | 5,000,000 | 50,000,000 | 50,000,000 | 100 GB/month | 25 GB |
| Enterprise | $999 | — | 10,000,000 | No limit | No limit | 250 GB/month | 100 GB |

Starter, Pro and Team can be billed monthly or yearly; a year costs ten monthly payments (two months free). A paid plan can only switch to a billing interval it is sold at; upgrades take effect immediately with a prorated charge and other changes apply at renewal.

## What is billed

- **Writes.** One operation unit is each started 4 KiB (4,096 bytes) of data successfully written; every changed row counts at least one unit. Only writes are billed.
- **Storage.** Stored bytes in the database's home region, sampled hourly. Replica storage is not billed separately. One GB-month is 1 GB (10^9 bytes) stored for a whole month.
- **Reads.** SQL reads are not billed; they count toward the plan's monthly fair use. Shape reads served from the edge are free and not counted.

Free includes small allowances without a card; new writes stop at its operation allowance and there are no overage charges. Paid plans include monthly allowances; operation units over the allowance are added to the next subscription invoice at the overage rate, up to the plan's hard cap (operationUnitCapPerMonth), where new writes stop until the next UTC month or an upgrade. SQL reads stop at the plan's fair use (sqlReadUnitsPerMonth); shape reads stay available. Storage over the allowance is added to the next invoice at the storage overage rate per GB-month of average stored bytes above it. Client egress over the allowance is not billed yet.

Overage rates on paid plans: $5 per million operation units and $0.5 per GB-month of storage.

When a plan's write cap or SQL read fair use is reached, the statement answers `429 quota_exceeded` (Postgres SQLSTATE `53100`) until the next UTC month or an upgrade.

## Regions per database

| Plan | Home regions per database |
| --- | --- |
| Free | 1 |
| Starter | 1 |
| Pro | 1 |
| Team | 1 |
| Enterprise | 1 |

Each database has one home region for writes and can have encrypted read replicas in other serving regions.
The catalog's regionsPerDatabase counts home regions. Replica storage is not billed separately; writes and stored
bytes are metered at home. See [regions](/de/concepts/regions) and [replication](/de/concepts/replication).

## Spending caps

Plan write caps are enforced. Customer-set hard dollar spending caps are coming. Internal report-only spending
thresholds do not stop services or cap a customer's bill.

See [support](/de/legal/support) for support targets.

</div>
