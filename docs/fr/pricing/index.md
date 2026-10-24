---
title: Pricing and limits
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Pricing and limits

The [pricing sheet](/fr/pricing) and [limits](/fr/limits) are generated from the public [pricing catalog](https://terrascale.tech/pricing.json) and documentation policy. Subscription prices, allowances and usage rates are approved. Free requires no card; Starter is $30/year only ($2.50/month equivalent), Pro $20/month, Team $50/month, and Enterprise $999/month support plus usage through sales. Operation units over a paid plan's allowance are added to the next subscription invoice at the overage rate; storage and client egress over the allowance are not billed yet. Free stops new writes at its operation allowance and has no overage charges.

## Operation units and storage

Each started 4 KiB of a logical object successfully read or written consumes an operation unit. Writes count once regardless of the number of selected regions; reads count only in the serving region. Replication between regions is not billed. Lists, queries and batches count each returned item independently. Failed, denied, invalid, throttled and not-found requests are not billed.

Storage counts once per selected region and uses decimal GB-region-month averaged over time during the month. Client egress is decimal outbound application response bytes, excluding replication traffic. Database and branch usage share the organization's plan allowances; there is no additional branch allowance. Exhausted operation units with covered storage do not start a storage-deletion grace period.

## Enrollment and billing policy

See the generated [pricing sheet](/fr/pricing) for plan amounts, eligibility, billing cycles and the proposed 30-day grace period. Confirm a deletion's exact scope and [export data you need](/fr/guides/export) before submitting it.

## API limits

The [limits page](/fr/limits) lists document and query limits and per-plan entitlements. Free maps to the hobby engine tier; other plans map to the tier of the same name. The per-plan request rates, storage limit, PITR window, backup retention and support response targets are in [plan entitlements](/fr/limits#plan-entitlements). See [SDK reference](/fr/guides/sdks) and [replication](/fr/concepts/replication) for SDK features and durability.

</div>
