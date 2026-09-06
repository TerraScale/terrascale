---
title: Terms of Service
description: TerraScale Terms of Service (draft).
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Terms of Service

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).

[PLACEHOLDER — O-4 written confirmations for OCI data processing and
residency, Cloudflare CDN/cache terms, and Stripe Tax account/configuration
are not on file. Do not publish until confirmed and reviewed by the owner.]
:::

These Terms of Service ("Terms") govern your use of TerraScale, provided by
[LEGAL ENTITY NAME — O-5], [JURISDICTION — O-5], [ADDRESS]. Effective
[EFFECTIVE DATE].

## Public launch

TerraScale launches with public self-service registration and database
creation, subject to automated account and abuse controls. There is no
waitlist, invitation requirement, or beta-credit program. Billing and data
durability apply from the first day of service.

## The service

TerraScale is a multi-region document database: writes replicate across the
Regions you select, reads are served near your users, and reactive queries
stream live results over a CDN. Launch Regions are GRU (São Paulo), SFO, and
FRA; you may select up to three Regions per database.

## Accounts and organizations

You may create organizations, invite members, and hold multiple databases.
You are responsible for your API keys and for what your members do. Paid
plans attach to an organization; free tier limits apply per organization.

## Acceptable use

Do not use the service to attack others, distribute malware, or break the
law. Abuse guards (rate limits, bandwidth and view caps) are enforced per
plan; exceeding one returns an error naming the fix — nothing is billed.
Sustained abuse may result in suspension.

## Pricing

Writes and storage are the only billed dimensions.

- **One write** = one create, update, patch, or delete of at most 4 KiB
  (`ceil(bytes / 4 KiB)` rounds larger documents up). A write is billed
  once, no matter how many clients, views, or subscribers see it.
- **Replication is priced honestly**: replicating a write to another
  selected region costs one additional write per region. There is no free
  per-region replication; the cost is shown up front, before you add a
  region.
- **Storage** = logical bytes (current documents plus retained history,
  TTL-aware) multiplied by the number of regions holding the database.
  Indexes, WAL, and backups are not billed.

Free and unbilled: reads, queries, egress, CDN delivery, fan-out, concurrent
subscribers, WebSocket connections, webhook delivery, catch-up and replay,
and TTL expiry. Guards on these free dimensions are unbilled abuse limits,
not charges.

Plan quotas, overage rates ($1 per 1M writes, $0.20 per GB-month per
region), and per-plan limits are published on the
[Pricing & limits](/de/pricing/) page.

## Billing

Payments are processed by Stripe. Stripe Tax is intended to be enabled from
day one to calculate applicable taxes; [PLACEHOLDER — Stripe Tax account,
configuration, and provider confirmation pending O-4; do not publish a claim
about tax treatment until confirmed]. Invoices, dunning, and receipts are
issued by Stripe. Overage is billed at the published rates; prices may only
go down. Spend caps, where set, are respected: a capped organization never
pays above its cap.

## Availability

We publish an SLO, not an SLA: a 99.9% monthly API availability target per
Region, measured by an external probe and shown live on our status page.
Incidents receive a public postmortem within 5 days. A contractual SLA with
credits is offered only on the Scale plan and only after launch.

## API stability

`/api/v1` is additive-only: fields and endpoints are added, never renamed or
removed. Deprecated APIs are announced in the changelog and kept for at
least 12 months. Breaking changes, if ever needed, ship under a separate versioned endpoint served
side by side.

## Your data and account deletion

You own your data. You may export the whole logical database at any time
yourself, without a support ticket, via the dashboard or CLI. Deleting your
account begins the following intended sequence: access and API keys are
revoked, new writes are stopped, Cloudflare cache entries tagged for the
database (including reactive snapshots and logs) are submitted for purge,
and the database's encryption keys are destroyed (crypto-shred). Destroying
the per-database keys renders database files, WAL, backups, exports, and
branches unreadable. Capability expiry prevents access while a CDN purge is
being processed; cache purge is an additional deletion control, not a promise
that an already-served response can be recalled.

Backups and legally required billing records follow their applicable
lifecycle. [PLACEHOLDER — exact Cloudflare purge completion behavior and OCI
backup-retention window are pending written provider confirmation (O-4) and
owner legal review.]

## Sub-processors

We process customer data on Oracle Cloud Infrastructure (hosting, storage,
backups, email delivery) and deliver authorized reactive data over
Cloudflare (DNS, TLS, WAF, CDN; edge decryption at the PoP nearest the
reader worldwide). [PLACEHOLDER — applicable Cloudflare DPA and CDN terms
pending O-4.] Billing and tax processing is
handled by Stripe (Stripe Tax is intended from day one). [PLACEHOLDER —
provider roles, applicable terms, and written confirmations are pending O-4;
see the marked draft tables in Privacy and the DPA.] See [Privacy
Policy](./privacy.md) and [DPA](./dpa.md) for the full sub-processor list
and security measures.

## Free-tier reaper

Free-tier databases with no request for 30 days are paused (kept, not
deleted) and the owner is emailed. After 90 days idle and further email
notices, they enter the account-deletion sequence above and are deleted
(including cache-tag purge and crypto-shred). Reactivating a paused database
takes one request. Paid plans are never reaped.

## Single-provider risk

Every customer byte lives in a single Oracle Cloud Infrastructure tenancy.
We accept this single-provider risk knowingly; it is a documented, accepted
risk we revisit at our first $10k MRR. Backups are distributed across your
selected Regions.

## Limitations of liability

[PLACEHOLDER — liability cap, exclusion of consequential damages, sole
remedy. Draft pending O-5.]

## Termination

[PLACEHOLDER — termination for convenience, termination for cause, data
export window before deletion. Draft pending O-5.]
