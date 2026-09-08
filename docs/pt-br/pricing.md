---
---
<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Draft; publication requires measured economics and owner approval. -->
# Pricing

> **DRAFT.** Publication awaits measured unit economics and owner approval
> of the final price sheet. These targets do not establish service readiness.
>
> **Pay for writes and storage; everything else is free.** Reads, queries,
> egress, CDN delivery, fan-out, subscribers, and webhook delivery are free
> and unbilled. Reads, queries, and direct member read egress are metered for
> abuse guards and cost attribution; a guard is never a charge. A write is billed once no matter how many views or
> subscribers it reaches. We never claim infrastructure is free:
> selected Regions multiply 4 KiB write units before included allowances,
> shown before Region selection.

## What you pay for

- **Writes.** One create/update/patch/delete of <= 4 KiB (ceil(bytes / 4 KiB)). Multiply 4 KiB write units by selected Regions before consuming included allowances; a 5 KiB write across three Regions consumes six units.
- **Storage.** Logical bytes (canonical bytes of current documents + retained history, TTL-aware, measured in byte-seconds) x regions, at $/GB-month.
- Not billed as storage: indexes, backups, service-managed storage overhead.
- Free and unbilled: reads, queries, egress, CDN delivery, fan-out, concurrent subscribers, WebSocket connections, webhook delivery, catch-up and replay, TTL expiry.
- Metered for abuse guards and cost attribution (never billed): reads, queries, direct member read egress.

## Plans

|  | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| price | $0 | $5 | $50 | $500 |
| writes | 100k writes/month | 1M writes/month | 100M writes/month | PAYG |
| storage | 100 MB | 5 GB | 25 GB | PAYG |
| databases | 1 | 5 | 25 | soft-unlimited |
| pitrDays | 1 | 7 | 30 | 35 |

Overage and PAYG: **$1 per 1M writes**, **$0.20 per GB-month per region** (Draft targets; increases require an owner decision. Publication awaits measured unit economics and owner approval.)

**Shared organization allowances.** Databases and branches share their owning organization’s normal plan allowances and billing cycle. There is no additional branch allowance or separate branch reset. Writes consume allowances after Region multiplication. 100 MB stored across three Regions consumes 300 MB of included storage, measured over time in byte-seconds.

**Free tier.** 1 database, 100 MB, **100,000 writes/month**, up to 3 regions. Free indefinitely with a valid verified card and automated fraud checks. Free eligibility and the first paid month promotion each have one entitlement per verified billing customer across organizations; only audited owner exceptions may expand eligibility.

**Scale.** The $500/month target is a paid support/compliance offering. Available benefits require confirmation before enrollment; priority response, SOC 2, and contractual SLA benefits are not available launch promises.

**Idle Free databases.** Free databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

## Billing cycle and promotion

Monthly write allowances reset on the organization’s billing anniversary, including Free, with no rollover. Storage remains measured over time; renewal does not erase data or continuing usage. The onboarding first paid month promotion waives only the subscription fee. Explicitly authorized usage beyond allowances remains chargeable; this must be explained before enrollment.

## Consent and subscription changes

Payments, invoices, consent, and spending controls belong to the organization. A stored card is not consent to overage. Customers must explicitly subscribe and enable paid excess after reviewing rates, allowances, and spending controls. Upgrades apply immediately with a preview of prorated charges and resulting allowances; they never enable paid excess automatically. Downgrades and cancellation apply at renewal. Preview resulting limits and preserve existing data; cancellation returns to Free only when eligible. Neither action silently deletes data or authorizes overage.

## Spending caps and exhausted allowances

One monthly organization spending cap covers every database, branch, and Region; optional lower database caps cannot raise it. At the cap, or when allowances are exhausted without paid excess consent, chargeable writes stop, including concurrent Regional usage. Reads and exports remain available within safety limits. Retained storage continues to accrue charges under agreed rates and consent, even while writes are restricted; show this exception before enabling a cap. Reaching a cap alone neither deletes data nor authorizes charges. Document deletes remain billed writes: restore paid write access before document cleanup. Account/database closure and retention deletion remain separate workflows.

## Unpaid storage

Storage without eligible allowance or payment coverage receives a 30-day grace period, advance dashboard and email notices, an exact visible deadline, and read/export access within safety limits. Deletion becomes eligible only while storage remains unpaid after the deadline and notice requirements are met. Authorized postpaid storage becomes unpaid when its invoice is due and payment fails, not when usage merely accrues. Retries do not reset the deadline. Restored eligible allowance or successful payment removes storage-related deletion eligibility. Exhausted writes with covered storage do not start this period.

## Card eligibility

Removing the required card, confirmed expiry, or permanent invalidity ends Free eligibility and starts the 30-day deletion grace period, even within the Free storage allowance. Warn before removal and show the deadline in dashboard and email. Temporary provider failures and replacement retaining a valid payment method do not establish card loss. Restoring a valid card stops the card-related countdown and restores Free access when otherwise eligible, without a paid subscription; outstanding charges and separate unpaid storage still need resolution. An already-paid term retains its coverage; the card-related countdown starts only when that coverage ends unless coverage or Free eligibility is restored.

## Deletion protection and completion

Deletion protection is opt-in and disabled by default. An owner must explicitly disable it before customer deletion. Protection prevents automatic idle-Free deletion, but not idle pause, unpaid-storage deletion, or required-card-loss deletion. Once authorized deletion is accepted, access is revoked immediately and further billable usage stops. Previously incurred charges remain payable; asynchronous physical cleanup must not create new usage charges. Deletion progress continues until retained copies are removed. Physical completion timing and final notices require verified retention behavior and owner approval.

See also: [Limits](./limits.md).
