---
title: Pricing & limits
description: TerraScale plans, quotas, write and storage definitions, abuse guards, and limits. Pay for writes and storage; reads, queries, egress, and delivery are free and unbilled.
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Pricing & limits

> **Draft.** The generated [pricing sheet](/hi/pricing) and [limits page](/hi/limits)
> are sourced from `data/pricing.json`. The values must be confirmed by the
> owner before publication, and every published number must have a
> measured unit cost behind it.

TerraScale bills exactly two dimensions: **writes and storage**. Reads,
queries, and direct member read egress are free and unbilled, but are metered
for abuse guards and cost attribution. The guards are limits, never charges.

## What counts as a write

One write = one create, update, patch, or delete of a document of **≤ 4 KiB**,
counted as `ceil(bytes / 4 KiB)` — a 5 KiB update is 2 writes.

**Replicated writes cost one write per region.** A one-unit write to a database
selected in 3 regions consumes 3 write allowance units; a 5 KiB write consumes 6. This is the per-region
replication cost, shown up front. We never claim infrastructure is free:
replication across your selected regions is priced as writes, above.
Delivering that write to any number of subscribers is free.

## What counts as storage

Storage = **logical bytes × regions**, at $/GB-month: the canonical bytes of
current documents plus retained history, TTL-aware, measured in byte-seconds.
Indexes, WAL, and backups are not billed.

## Free and unbilled

These dimensions are never billed. Reads, queries, and direct member read
egress are metered for abuse guards and cost attribution; the limits below
apply where a guard exists. CDN-delivered reactive data has no bandwidth guard.

- reads
- queries
- egress
- CDN delivery
- fan-out
- concurrent subscribers
- WebSocket connections
- webhook delivery
- catch-up and replay
- TTL expiry

## Plans

| | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| Monthly price | $0 | $5 | $50 | $500 |
| Writes / month | 100k writes/month | 1M writes/month included | 100M writes/month included | PAYG |
| Storage | 100 MB | 5 GB included | 25 GB included | PAYG |
| Regions per database | Up to 3 | Up to 3 | Up to 3 | Up to 3 |
| Databases per org | 1 | 5 | 25 | soft-unlimited |
| PITR retention | 1 day | 7 days | 30 days | 35 days |

**Scale.** The $500/month target is a paid support/compliance offering. Available benefits require confirmation before enrollment; priority response, SOC 2, and contractual SLA benefits are not available launch promises.

Support channels and response commitments must be confirmed before enrollment;
email intake and staffed response coverage are not advertised as available.

## Overage & PAYG

- **$1 per 1M** writes
- **$0.20 per GB-month** per region

These are draft targets; any change requires an owner decision, measured unit
economics, and publication approval.

## Shared organization allowances

Databases and branches share their owning organization’s normal plan allowances and billing cycle. There is no additional branch allowance or separate branch reset. Writes consume allowances after Region multiplication. 100 MB stored across three Regions consumes 300 MB of included storage, measured over time in byte-seconds.

## Guards (unbilled abuse limits)

Per database, owner-tunable. These are abuse guards, not prices: nothing here
is billed, and exceeding one returns a typed error with the fix.

| Guard (per database) | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| Read + query requests/s | 100 | 1,000 | 5,000 | custom |
| Direct member read bandwidth / month | 50 GB | 500 GB | 2 TB | custom |
| Distinct active shared views | 1k | 10k | 100k | custom |
| WebSocket connections | 100 | 1,000 | 10,000 | custom |
| Webhook endpoints | 3 | 10 | 50 | custom |

Direct member read bandwidth means bytes leaving OCI for reads served by
members, not by the CDN. **CDN-delivered reactive data has no bandwidth
guard.** Subscribers per view are unlimited on every plan.

## Other limits

- Max document size: **1 MiB**
- History retention: default 7 days, configurable per collection 1–30 days,
  plan-capped at the PITR figure, billed as storage
- **Placement lock (residency):** a database's bytes at rest exist only in
  its selected regions. A placement lock (`eu-only`, `br-only`, or an
  explicit region list) is set at creation and cannot be widened; it is
  enforced at placement, replication, backup, restore, export, and
  branching. Cloudflare's edge decrypts reactive traffic at the PoP nearest
  the reader worldwide as a sub-processor under its DPA; we do not promise
  in-region edge processing at launch.

## Free eligibility and idle databases

Free indefinitely with a valid verified card and automated fraud checks. Free eligibility and the first paid month promotion each have one entitlement per verified billing customer across organizations; only audited owner exceptions may expand eligibility.

Free databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

## How billing works

Engine usage facts are the source of truth. A write is billed once, regardless
of how many shared views it affects or how many clients are attached. Optional
WebSocket concurrency and distinct-view caps are unbilled abuse guards, not
prices.

See [pricing philosophy](/hi/concepts/pricing-philosophy) for why the model looks
like this. Billing starts with public self-service launch.

Generated detail: [pricing sheet](/hi/pricing) · [limits](/hi/limits).

## Card verification and paid usage

A valid verified card is required before creating a database, including on
Free. Automated fraud checks apply. Free has no time limit while the account
remains eligible and usage stays within its allowance. Customers can subscribe
and explicitly enable paid usage above that allowance. A stored card alone
does not authorize overage charges; review the rates and spend controls first.


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
