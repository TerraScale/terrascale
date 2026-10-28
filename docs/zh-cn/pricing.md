---
---
<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Pricing

> **Approved subscription prices, allowances and usage rates.** Prices come from the catalog at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json); the
> [pricing page](https://terrascale.tech/pricing) renders the same catalog.
> All prices are in USD. Free needs only sign-up; Starter, Pro and Team are bought through
> checkout; Enterprise is arranged through sales.

## How usage is metered

- **Operation units.** One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once regardless of the number of selected regions; reads count only in the serving region. Replication between regions is not billed. Failed, denied, invalid, throttled, and not-found requests are not billed.
- **Storage across regions.** Stored bytes count once in every selected region. Storage uses decimal GB-region-month, averaged over time during the month.
- **Client egress.** Outbound application response bytes to clients, excluding replication traffic; decimal GB.
- **Allowances.** Free includes small allowances without a card; new writes stop at its operation allowance and there are no overage charges. Paid plans include monthly allowances; operation units over the allowance are added to the next subscription invoice at the overage rate. Storage and client egress over the allowance are not billed yet.

## Plans and allowances

|  | Free | Starter | Pro | Team | Enterprise |
| --- | --- | --- | --- | --- | --- |
| Subscription | $0; no card | $3/month or $30/year | $20/month or $200/year | $50/month or $500/year | $999/month support + usage; contact sales |
| Availability | Sign up | Checkout | Checkout | Checkout | Contact sales |
| Projects | 1 | 5 | 25 | 100 | Custom |
| Databases | 1 | 5 | 25 | 100 | Custom |
| Members | 1 | 5 | 25 | 100 | Custom |
| Storage (GB-region-month) | 100 MB | 1 GB | 10 GB | 25 GB | 100 GB |
| Operation units / month | 50,000 | 250,000 | 2,000,000 | 5,000,000 | 10,000,000 |
| Client egress / month | 1 GB | 5 GB | 50 GB | 100 GB | 250 GB |
| Regions per database | up to 3 | up to 3 | up to 3 | up to 3 | up to 3 |

Launch regions: GRU (São Paulo), SJC (San Francisco Bay Area), FRA (Frankfurt).

Request rates, concurrency, query timeout, storage limit, point-in-time recovery, backup
retention and support response targets per plan are in [plan entitlements](./limits.md#plan-entitlements).

Starter, Pro and Team are each billed monthly or yearly; a year costs ten monthly payments (two months free). No trials are offered.

## Pay-as-you-go rates

Paid plans include the allowances above. Operation units over the allowance are added to the next
subscription invoice at the rate below. Storage and client egress over the allowance are not billed yet.

| Meter | Rate (USD) |
| --- | --- |
| Operation units | $5 per million |
| Storage | $0.50 per GB-region-month |
| Client egress | $0.10 per decimal GB |

**Shared organization allowances.** Databases and branches share their owning organization’s plan allowances. There is no additional branch allowance. Each 4 KiB written counts once, no matter how many regions hold the database; reads count in the region that serves them; replication between regions is free. Storage is decimal GB-region-month, averaged over time: 100 MB stored across three regions for a full month consumes 0.3 GB-region-month.

**Free.** Free requires no payment card and has no overage charges. No trial or first-paid-month promotion is offered by this catalog.

**Enterprise.** $999 per month support plus usage; contact sales. Enterprise is arranged through sales and is not sold through checkout. SOC 2 and contractual SLA benefits are not published launch promises.

## Billing cycle

Starter, Pro and Team are billed monthly or yearly (a year costs ten monthly payments); operation-unit overage is invoiced every month on both. Enterprise support plus usage is arranged through sales. Operation-unit and client-egress allowances are monthly, including on Starter; they are not annual usage pools. Storage is averaged over time during the month. Operation units over a paid allowance are added to the next subscription invoice. No trials are offered.

## Subscription changes

Starter, Pro and Team are bought and changed through checkout; Enterprise is arranged through sales. A change of plan must preserve existing data; cancellation is not a deletion request.

## Exhausted allowances

Free stops new writes at its operation allowance and has no overage charges. On paid plans, operation units over the allowance are added to the next subscription invoice at the overage rate; storage and client egress over the allowance are not billed yet. Reaching an allowance alone does not authorize deletion; account/database closure and retention deletion are separate workflows.

## Unpaid storage

Proposed policy; confirm availability and enrollment terms. Storage without eligible allowance or payment coverage receives a 30-day grace period, advance dashboard and email notices, an exact visible deadline, and read/export access within safety limits. Deletion becomes eligible only while storage remains unpaid after the deadline and notice requirements are met. Retries do not reset the deadline. Restored eligible allowance or successful payment removes storage-related deletion eligibility. Exhausted operation units with covered storage do not start this period.

## Card eligibility

Free does not require a payment card. Removing or losing a card does not by itself end Free eligibility or start a card-related deletion countdown. Paid subscriptions require the payment coverage agreed at enrollment.

## Deletion protection and completion

Deletion protection is opt-in and disabled by default. An owner must explicitly disable it before customer deletion. Protection prevents automatic idle-Free deletion, but not idle pause or unpaid-storage deletion. Once authorized deletion is accepted, access is revoked immediately and further billable usage stops. Previously incurred charges remain payable; asynchronous physical cleanup must not create new usage charges. Deletion progress continues until retained copies are removed. Physical completion timing and final notices require verified retention behavior and owner approval.

See also: [Limits](./limits.md).

</div>
