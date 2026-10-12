---
title: Pricing and limits
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Pricing and limits

The [pricing sheet](/ru/pricing) and [limits](/ru/limits) are generated from the public [pricing catalog](https://terrascale.tech/pricing.json) and documentation policy. Public Alpha uses hard allowances with no paid overages. Confirm final enrollment terms and serving-release availability before relying on a proposed policy.

## Operation units and storage

Each started 4 KiB of a logical object successfully read or written consumes an operation unit. Writes count once per selected region; reads count only in the serving region. Lists, queries and batches count each returned item independently. Failed, denied, invalid, throttled and not-found requests are not billed.

Storage counts once per selected region. Database and branch usage share the organization's plan allowances; there is no additional branch allowance. Exhausted operation units with covered storage do not start a storage-deletion grace period.

## Enrollment and billing policy

See the generated [pricing sheet](/ru/pricing) for plan amounts, eligibility, billing cycles and the proposed 30-day grace period. These policies do not qualify hosted provisioning, automatic deletion or recovery behavior. Confirm an authorized deletion's exact scope and preserve required data before submitting it.

## API limits and qualification

Document limits, query limits and overload errors depend on the serving contract. The [limits page](/ru/limits) separates the Documents API from plan policy. Availability of an allowance does not prove an SDK feature or production guarantee.

Review [SDK availability](/ru/guides/sdks) and [replication boundaries](/ru/concepts/replication) separately.

</div>
