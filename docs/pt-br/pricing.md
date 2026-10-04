---
---
<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Pricing

> **Public Alpha.** Prices and allowances come from the canonical catalog at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json); the
> [pricing page](https://terrascale.tech/pricing) renders the same catalog.
> All prices are in USD.

## How usage is metered

- **Operation units.** One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once per selected region; reads count only in the serving region. Failed, denied, invalid, throttled, and not-found requests are not billed.
- **Storage across regions.** Stored bytes count once in every selected region.
- **Allowances.** Plans use hard allowances during Public Alpha. New billable work stops at the allowance. Reads, exports, deletion, and usage reduction remain available.

## Plans

|  | Hobby | Starter | Pro | Enterprise |
| --- | --- | --- | --- | --- |
| Monthly price | $0 | $5 | $50 | $500 |
| Annual price | $0 | $50 | $500 | $5,000 |
| Projects | 1 | 5 | 25 | Custom |
| Databases | 1 | 5 | 25 | Custom |
| Members | 1 | 5 | 25 | Custom |
| Storage across regions | 100 MB | 5 GB | 25 GB | Custom |
| Operation units / month | 100,000 | 1,000,000 | 100,000,000 | Custom |
| Regions per database | up to 3 | up to 3 | up to 3 | up to 3 |
| PITR retention | 1 day | 7 days | 30 days | 35 days |

Launch regions: GRU (São Paulo), SFO (San Jose), FRA (Frankfurt).

Starter and Pro include one eligible 30-day trial per verified payment method and organization.

**Shared organization allowances.** Databases and branches share their owning organization’s normal plan allowances and billing cycle. There is no additional branch allowance or separate branch reset. Writes consume operation units once per selected region before allowances. 100 MB stored across three regions consumes 300 MB of included storage.

**Hobby.** Hobby has no time limit with a valid verified card and automated fraud checks after public launch. Each verified billing customer has one Hobby entitlement across all organizations: creating or joining another organization does not grant another Hobby plan. The first paid month promotion is likewise limited to one entitlement across organizations; only audited owner exceptions may expand eligibility.

**Enterprise.** Monthly support base plus metered usage. Enterprise is a paid support and compliance offering and is unavailable during Public Alpha. Available benefits require confirmation before enrollment; priority response, SOC 2, and contractual SLA benefits are not available launch promises.

**Idle Hobby databases.** Hobby databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

## Billing cycle and promotion

Monthly operation-unit allowances reset on the organization’s billing anniversary, including Hobby, with no rollover. Storage remains measured over time; renewal does not erase data or continuing usage. The onboarding first paid month promotion waives only the subscription fee.

## Subscription changes

Payments, invoices, and spending controls belong to the organization. Upgrades apply immediately with a preview of prorated charges and resulting allowances. Downgrades and cancellation apply at renewal. Preview resulting limits and preserve existing data; cancellation returns to Hobby only when eligible. Neither action silently deletes data.

## Exhausted allowances

Public Alpha has no paid overages. When an allowance is exhausted, new billable work stops, including concurrent regional usage. Reads, exports, deletion, and usage reduction remain available within safety limits. Reaching an allowance alone neither deletes data nor creates charges. Account/database closure and retention deletion remain separate workflows.

## Unpaid storage

Storage without eligible allowance or payment coverage receives a 30-day grace period, advance dashboard and email notices, an exact visible deadline, and read/export access within safety limits. Deletion becomes eligible only while storage remains unpaid after the deadline and notice requirements are met. Retries do not reset the deadline. Restored eligible allowance or successful payment removes storage-related deletion eligibility. Exhausted operation units with covered storage do not start this period.

## Card eligibility

Removing the required card, confirmed expiry, or permanent invalidity ends Hobby eligibility and starts the 30-day deletion grace period, even within the Hobby storage allowance. Warn before removal and show the deadline in dashboard and email. Temporary provider failures and replacement retaining a valid payment method do not establish card loss. Restoring a valid card stops the card-related countdown and restores Hobby access when otherwise eligible, without a paid subscription; outstanding charges and separate unpaid storage still need resolution. An already-paid term retains its coverage; the card-related countdown starts only when that coverage ends unless coverage or Hobby eligibility is restored.

## Deletion protection and completion

Deletion protection is opt-in and disabled by default. An owner must explicitly disable it before customer deletion. Protection prevents automatic idle-Hobby deletion, but not idle pause, unpaid-storage deletion, or required-card-loss deletion. Once authorized deletion is accepted, access is revoked immediately and further billable usage stops. Previously incurred charges remain payable; asynchronous physical cleanup must not create new usage charges. Deletion progress continues until retained copies are removed. Physical completion timing and final notices require verified retention behavior and owner approval.

See also: [Limits](./limits.md).
