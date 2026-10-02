---
title: Terms of Service
description: TerraScale Terms of Service (draft).
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Terms of Service

::: warning Draft — not yet in effect
This document is an unpublished draft. It is not yet in effect and does not
govern your use of TerraScale.
:::

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
stream live results over a CDN. Launch Regions are GRU (São Paulo), SFO (San Jose),
and FRA (Frankfurt); you may select up to three Regions per database.

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

Plans are metered in operation units and storage across regions.

- **One operation unit** is each started 4 KiB (4,096 bytes) of a logical
  object successfully read or written. Lists, queries, and batches count
  each item independently. Writes count once per selected Region; reads
  count only in the serving Region. Failed, denied, invalid, throttled, and
  not-found requests are not billed.
- **Storage**: stored bytes count once in every selected Region. Indexes
  and backups do not count toward storage.

During Public Alpha, plans use hard allowances and there are no paid
overages: new billable work stops at the allowance, while reads, exports,
deletion, and usage reduction remain available. Guards are unbilled safety
limits, not charges.

Plan prices, allowances, and per-plan limits are listed on the
[Pricing & limits](/hi/pricing/) page.

## Billing

Payments are processed by Stripe. Stripe Tax is intended to be enabled from
day one to calculate applicable taxes; [PLACEHOLDER — Stripe Tax account,
configuration, and provider confirmation pending O-4; do not publish a claim
about tax treatment until confirmed]. Invoices, dunning, and receipts are
issued by Stripe. Overage requires explicit paid-excess consent after reviewing rates and
allowances; a stored card alone is not consent. Organization caps stop
chargeable writes, but retained storage continues to accrue agreed charges.
See [billing policies](/hi/pricing/) for shared branch allowances, promotion
eligibility, subscription changes, and separate 30-day unpaid-storage and
card-related grace periods. Prices remain drafts awaiting approval.

## Availability

The launch objective is 99.9% monthly API availability per Region, measured
by external probes. This is an SLO, not a contractual SLA. Public status and
metrics pages are deferred. Contractual SLA credits, staffed response times,
and fixed public postmortem deadlines are not launch promises.

## API stability

`/api/v1` is additive-only: fields and endpoints are added, never renamed or
removed. Deprecated APIs are announced in the changelog and kept for at
least 12 months. Breaking changes, if ever needed, ship under a separate versioned endpoint served
side by side.

## Your data and account deletion

You own your data. Export and recovery workflows are being qualified against
their producer contracts; dashboard and CLI availability is not promised until
that qualification is complete. When account deletion is authorized, access
and API keys are revoked, new writes stop, and retained copies are removed
under the applicable deletion workflow. Access remains blocked while retained
copies are being removed. Previously incurred charges and legally required
records follow their applicable lifecycle.

 [PLACEHOLDER — exact retained-copy completion behavior and backup-retention
window are pending written provider confirmation (O-4) and owner legal review.]

## Sub-processors

We process customer data through the listed hosting, delivery, identity, and
billing providers. [PLACEHOLDER — applicable provider roles, locations, and
processing terms, including CDN and tax terms, pending O-4.] Billing and tax processing is
handled by Stripe (Stripe Tax is intended from day one). [PLACEHOLDER —
provider roles, applicable terms, and written confirmations are pending O-4;
see the marked draft tables in Privacy and the DPA.] See [Privacy
Policy](./privacy.md) and [DPA](./dpa.md) for the full sub-processor list
and security measures.

## Hobby-plan reaper

Hobby databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

Deletion protection is opt-in and disabled by default. An owner must explicitly disable it before customer deletion. Protection prevents automatic idle-Hobby deletion, but not idle pause, unpaid-storage deletion, or required-card-loss deletion. Once authorized deletion is accepted, access is revoked immediately and further billable usage stops. Previously incurred charges remain payable; asynchronous physical cleanup must not create new usage charges. Deletion progress continues until retained copies are removed. Physical completion timing and final notices require verified retention behavior and owner approval.

See the draft [billing policy](/hi/pricing/) for separate unpaid-storage and
required-card grace periods. These exceptions are not overridden by protection.

## Limitations of liability

[PLACEHOLDER — liability cap, exclusion of consequential damages, sole
remedy. Draft pending O-5.]

## Termination

[PLACEHOLDER — termination for convenience, termination for cause, data
export window before deletion. Draft pending O-5.]
