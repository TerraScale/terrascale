---
title: Pricing and limits
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Pricing and limits

The [pricing sheet](/es/pricing) and [limits](/es/limits) are generated from the public [pricing catalog](https://terrascale.tech/pricing.json) and documentation policy. Public Alpha uses hard allowances with no paid overages.

## Operation units and storage

Each started 4 KiB of a logical object successfully read or written consumes an operation unit. Writes count once per selected region; reads count only in the serving region. Lists, queries and batches count each returned item independently. Failed, denied, invalid, throttled and not-found requests are not billed.

Storage counts once per selected region. Database and branch usage share the organization's plan allowances; there is no additional branch allowance. Exhausted operation units with covered storage do not start a storage-deletion grace period.

## Enrollment and billing policy

See the generated [pricing sheet](/es/pricing) for plan amounts, eligibility, billing cycles and the proposed 30-day grace period. Confirm a deletion's exact scope and [export data you need](/es/guides/export) before submitting it.

## API limits

The [limits page](/es/limits) lists document, query and plan limits. See [SDK reference](/es/guides/sdks) and [replication](/es/concepts/replication) for SDK features and durability.

</div>
