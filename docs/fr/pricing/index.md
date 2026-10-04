---
title: Pricing & limits
description: TerraScale plans, allowances, operation units, storage across regions, abuse guards, and limits.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Pricing & limits

> The generated [pricing sheet](/fr/pricing) and [limits page](/fr/limits) are
> built from the canonical catalog published at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json), the
> same source as the [pricing page](https://terrascale.tech/pricing). A
> vendored copy keeps offline builds working, and CI fails when it drifts.
>
> **Public Alpha.** Plans use hard allowances. There are no paid overages.

TerraScale meters two things: **operation units** and **storage across
regions**. Each plan includes a monthly operation-unit allowance and a
storage allowance.

## What counts as an operation unit

One operation unit is each started 4 KiB (4,096 bytes) of a logical object
successfully read or written. A 5 KiB document is 2 units.

- Lists, queries, and batches count each item independently.
- Writes count once per selected region. A one-unit write to a database in
  3 regions consumes 3 units, and a 5 KiB write consumes 6.
- Reads count only in the serving region.
- Failed, denied, invalid, throttled, and not-found requests are not billed.

## What counts as storage

Stored bytes count once in every selected region. 100 MB stored across three
regions consumes 300 MB of included storage.

## Plans

| | Hobby | Starter | Pro | Enterprise |
| --- | --- | --- | --- | --- |
| Monthly price | $0 | $5 | $50 | $500 |
| Annual price | $0 | $50 | $500 | $5,000 |
| Projects | 1 | 5 | 25 | Custom |
| Databases | 1 | 5 | 25 | Custom |
| Members | 1 | 5 | 25 | Custom |
| Storage across regions | 100 MB | 5 GB | 25 GB | Custom |
| Operation units / month | 100,000 | 1,000,000 | 100,000,000 | Custom |
| Regions per database | Up to 3 | Up to 3 | Up to 3 | Up to 3 |
| PITR retention | 1 day | 7 days | 30 days | 35 days |

Launch regions are GRU (São Paulo), SFO (San Jose), and FRA (Frankfurt).

Starter and Pro include one eligible 30-day trial per verified payment method
and organization.

**Enterprise.** Monthly support base plus metered usage. Enterprise is
unavailable during Public Alpha. Available benefits require confirmation
before enrollment; priority response, SOC 2, and contractual SLA benefits are
not available launch promises.

Support channels and response commitments must be confirmed before enrollment;
email intake and staffed response coverage are not advertised as available.

## When an allowance runs out

New billable work stops at the allowance. Reads, exports, deletion, and usage
reduction remain available. Public Alpha does not offer paid overages, so
reaching an allowance never creates a charge.

## Shared organization allowances

Databases and branches share their owning organization’s normal plan
allowances and billing cycle. There is no additional branch allowance or
separate branch reset.

## Guards (unbilled safety limits)

Per database, owner-tunable. Guards apply alongside plan allowances. Nothing
here is billed, and exceeding one returns a typed error with the fix.

| Guard (per database) | Hobby | Starter | Pro | Enterprise |
| --- | --- | --- | --- | --- |
| Read + query requests/s | 100 | 1,000 | 5,000 | custom |
| Direct member read bandwidth / month | 50 GB | 500 GB | 2 TB | custom |
| Distinct active shared views | 1,000 | 10,000 | 100,000 | custom |
| WebSocket connections | 100 | 1,000 | 10,000 | custom |
| Webhook endpoints | 3 | 10 | 50 | custom |

Direct member read bandwidth is the allowance for reads served directly by
database members, rather than through the CDN. **CDN-delivered reactive data
has no bandwidth guard.** Subscribers per view are unlimited on every plan.

## Other limits

- Max document size: **1 MiB**
- History retention: default 7 days, configurable per collection 1–30 days,
  plan-capped at the PITR figure, counted as storage
- **Placement lock (residency):** a database's bytes at rest exist only in
  its selected regions. A placement lock (`eu-only`, `br-only`, or an
  explicit region list) is set at creation and cannot be widened; it is
  enforced at placement, replication, backup, restore, export, and
  branching. Reactive delivery may be processed through the delivery network;
  in-region edge processing is not promised at launch.

## Hobby eligibility and idle databases

Hobby has no time limit with a valid verified card and automated fraud checks
after public launch. Each verified billing customer has one Hobby entitlement
across all organizations: creating or joining another organization does not
grant another Hobby plan. The first paid month promotion is likewise limited
to one entitlement across organizations; only audited owner exceptions may
expand eligibility.

Hobby databases pause after 30 idle days; a request resumes them. They may be
deleted after 90 idle days with advance notices unless deletion protection is
enabled. Paid databases are never idle-reaped. The final notice schedule
requires owner approval.

## How billing works

Metered usage facts are the source of truth. A write counts once per selected
region, regardless of how many shared views it affects or how many clients
are attached. Optional WebSocket concurrency and distinct-view caps are
unbilled abuse guards, not prices.

See [pricing philosophy](/fr/concepts/pricing-philosophy) for why the model looks
like this.

Generated detail: [pricing sheet](/fr/pricing) · [limits](/fr/limits).

## Card verification

A valid verified card is required before creating a database, including on
Hobby. Automated fraud checks apply. Hobby has no time limit while the account
remains eligible and usage stays within its allowance. A stored card alone
never authorizes charges beyond your plan.

## Billing cycle and promotion

Monthly operation-unit allowances reset on the organization’s billing
anniversary, including Hobby, with no rollover. Storage remains measured over
time; renewal does not erase data or continuing usage. The onboarding first
paid month promotion waives only the subscription fee.

## Subscription changes

Payments, invoices, and spending controls belong to the organization.
Upgrades apply immediately with a preview of prorated charges and resulting
allowances. Downgrades and cancellation apply at renewal. Preview resulting
limits and preserve existing data; cancellation returns to Hobby only when
eligible. Neither action silently deletes data.

## Unpaid storage

Storage without eligible allowance or payment coverage receives a 30-day grace period, advance dashboard and email notices, an exact visible deadline, and read/export access within safety limits. Deletion becomes eligible only while storage remains unpaid after the deadline and notice requirements are met. Retries do not reset the deadline. Restored eligible allowance or successful payment removes storage-related deletion eligibility. Exhausted operation units with covered storage do not start this period.

## Card eligibility

Removing the required card, confirmed expiry, or permanent invalidity ends Hobby eligibility and starts the 30-day deletion grace period, even within the Hobby storage allowance. Warn before removal and show the deadline in dashboard and email. Temporary provider failures and replacement retaining a valid payment method do not establish card loss. Restoring a valid card stops the card-related countdown and restores Hobby access when otherwise eligible, without a paid subscription; outstanding charges and separate unpaid storage still need resolution. An already-paid term retains its coverage; the card-related countdown starts only when that coverage ends unless coverage or Hobby eligibility is restored.

## Deletion protection and completion

Deletion protection is opt-in and disabled by default. An owner must explicitly disable it before customer deletion. Protection prevents automatic idle-Hobby deletion, but not idle pause, unpaid-storage deletion, or required-card-loss deletion. Once authorized deletion is accepted, access is revoked immediately and further billable usage stops. Previously incurred charges remain payable; asynchronous physical cleanup must not create new usage charges. Deletion progress continues until retained copies are removed. Physical completion timing and final notices require verified retention behavior and owner approval.
