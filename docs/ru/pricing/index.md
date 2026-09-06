---
title: Pricing & limits
description: TerraScale plans, quotas, write and storage definitions, abuse guards, and limits. Pay for writes and storage; reads, queries, egress, and delivery are free and unbilled.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

# Pricing & limits

> **Draft.** The generated [pricing sheet](/ru/pricing) and [limits page](/ru/limits)
> are sourced from `data/pricing.json`. The values must be confirmed by the
> owner before publication (O-6), and every published number must have a
> measured unit cost behind it (D-61).

TerraScale bills exactly two dimensions: **writes and storage**. Reads,
queries, and direct member read egress are free and unbilled, but are metered
for abuse guards and cost attribution. The guards are limits, never charges.

## What counts as a write

One write = one create, update, patch, or delete of a document of **≤ 4 KiB**,
counted as `ceil(bytes / 4 KiB)` — a 5 KiB update is 2 writes.

**Replicated writes cost one write per region.** A write to a database
selected in 3 regions is billed as 3 writes. This is the per-region
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
| Writes / month | 100k | 1M included | 100M included | PAYG |
| Storage | 100 MB | 5 GB included | 25 GB included | PAYG |
| Regions per database | Up to 3 | Up to 3 | Up to 3 | Up to 3 |
| Databases per org | 1 | 5 | 25 | soft-unlimited |
| PITR retention | 1 day | 7 days | 30 days | 35 days |
| Support | Community (Discord) | Email | Email | Shared channel with response-time targets |

**Scale** is a support-and-compliance fee: priority support, SOC 2 report
access, security questionnaires, and DPA/SLA — with every write and GB billed
at PAYG rates.

## Overage & PAYG

- **$1 per 1M** writes
- **$0.20 per GB-month** per region

Both may only go down.

## Branch allowance

Every org, on any plan, gets **100k writes/month + 1 GB** shared across all of
its branches (dev branches included), on top of plan quotas. Branch usage
beyond that allowance bills like a database.

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

## The free tier, stated honestly

- A card is required at signup (fraud screening + rate limits); it is
  effortless to continue without a plan.
- First paid month free if you subscribe during onboarding.
- **Free-tier reaper:** a Free database with no request for 30 days is paused
  (closed, backup stream stopped, snapshot kept) and the owner is emailed; at
  90 days idle, after two more emails, it is deleted (crypto-shred). Paid
  plans are never reaped. Reactivation is one request.

## How billing works

Engine usage facts are the source of truth. A write is billed once, regardless
of how many shared views it affects or how many clients are attached. Optional
WebSocket concurrency and distinct-view caps are unbilled abuse guards, not
prices.

See [pricing philosophy](/ru/concepts/pricing-philosophy) for why the model looks
like this. Billing starts with public self-service launch.

Generated detail: [pricing sheet](/ru/pricing) · [limits](/ru/limits).

## Card verification and paid usage

A valid verified card is required before creating a database, including on
Free. Automated fraud checks apply. Free has no time limit while the account
remains eligible and usage stays within its allowance. Customers can subscribe
and explicitly enable paid usage above that allowance. A stored card alone
does not authorize overage charges; review the rates and spend controls first.
