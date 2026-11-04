---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

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
- **Storage.** Stored bytes in the database's home region, sampled hourly. Replica storage is not billed separately. One GB-month is 1 GB (10^9 bytes) stored for a whole month.
- **Reads.** SQL reads are not billed; they count toward the plan's monthly fair use. Shape reads served from the edge are free and not counted.

Free includes small allowances without a card; new writes stop at its operation allowance and there are no overage charges. Paid plans include monthly allowances; operation units over the allowance are added to the next subscription invoice at the overage rate, up to the plan's hard cap (operationUnitCapPerMonth), where new writes stop until the next UTC month or an upgrade. SQL reads stop at the plan's fair use (sqlReadUnitsPerMonth); shape reads stay available. Storage over the allowance is added to the next invoice at the storage overage rate per GB-month of average stored bytes above it. Client egress over the allowance is not billed yet.

Overage rates on paid plans: $5 per million write units and $0.5 per GB-month of storage.

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
bytes are metered at home. See [regions](/hi/concepts/regions) and [replication](/hi/concepts/replication).

## Spending caps

Plan write caps are enforced. Customer-set hard dollar spending caps are coming. Internal report-only spending
thresholds do not stop services or cap a customer's bill.

See [support](/hi/legal/support) for support targets.

</div>
