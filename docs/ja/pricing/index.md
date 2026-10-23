---
title: Pricing and limits
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Pricing and limits

The [pricing sheet](/ja/pricing) and [limits](/ja/limits) are generated from the public [pricing catalog](https://terrascale.tech/pricing.json) and documentation policy. Subscription prices, allowances and usage rates are approved. Free requires no card; Starter is $30/year only ($2.50/month equivalent), Pro $20/month, Team $50/month, and Enterprise $999/month support plus usage through sales. Paid plans include allowances then pay-as-you-go; usage charges are not yet collected.

## Operation units and storage

Each started 4 KiB of a logical object successfully read or written consumes an operation unit. Writes count once per selected region; reads count only in the serving region. Lists, queries and batches count each returned item independently. Failed, denied, invalid, throttled and not-found requests are not billed.

Storage counts once per selected region and uses decimal GB-region-month averaged over time during the month. Client egress is decimal outbound application response bytes, excluding replication traffic. Database and branch usage share the organization's plan allowances; there is no additional branch allowance. Exhausted operation units with covered storage do not start a storage-deletion grace period.

## Enrollment and billing policy

See the generated [pricing sheet](/ja/pricing) for plan amounts, eligibility, billing cycles and the proposed 30-day grace period. Confirm a deletion's exact scope and [export data you need](/ja/guides/export) before submitting it.

## API limits

The [limits page](/ja/limits) lists document, query and engine safety tiers. These tiers are distinct from commercial plans; no mapping is published. See [SDK reference](/ja/guides/sdks) and [replication](/ja/concepts/replication) for SDK features and durability.

</div>
