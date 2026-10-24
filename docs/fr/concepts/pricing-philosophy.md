---
title: Usage accounting
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Usage accounting

The public catalog meters operation units, storage across selected regions and client egress. Each started 4 KiB successfully read or written counts as one operation unit. Writes count once regardless of the number of selected regions; reads count only in the serving region. Replication between regions is not billed. Denied and failed requests are not billed.

Plan allowances are shared at the organization level. Check the [canonical pricing sheet](/fr/pricing) and [limits](/fr/limits) before enrollment.

Storage uses decimal GB-region-month averaged over time during the month. Client egress counts decimal outbound application response bytes and excludes replication. Subscription prices, allowances and rates are approved. Operation units over a paid plan's allowance are added to the next subscription invoice at the overage rate; storage and client egress over the allowance are not billed yet. Free requires no card, stops new writes at its operation allowance and has no overage charges.

</div>
