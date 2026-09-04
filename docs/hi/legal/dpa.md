---
title: Data Processing Addendum
description: TerraScale Data Processing Addendum (draft).
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Data Processing Addendum

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).
:::

This Data Processing Addendum ("DPA") is between [LEGAL ENTITY NAME — O-5],
[JURISDICTION — O-5], [ADDRESS] ("Processor") and the customer ("Controller").
Effective [EFFECTIVE DATE].

## Scope

Customers store application data in TerraScale databases. For that content,
we act as processor; the customer is the controller. Account and billing
data (processed via Stripe, with Stripe Tax on from day one) are processed
under our [Privacy Policy](./privacy.md).

## Sub-processors

| Sub-processor | Processing | Locations |
| --- | --- | --- |
| Oracle Cloud Infrastructure | Compute hosts, block storage, object storage (backups), email delivery | Tenancy in [JURISDICTION(S)] with GRU home region |
| Cloudflare | Edge network: DNS, TLS termination, WAF, CDN cache and delivery of authorized reactive data; edge decryption at the PoP nearest the reader worldwide | Global edge network |

We do not transfer customer data to any other provider. The reactive
delivery contract is plain HTTP caching semantics, so the CDN is replaceable
(a Bunny.net fallback is documented) without changing the security posture.

## Security measures

- Per-database data encryption keys (DEKs), wrapped by per-region key
  encryption keys (KEKs) in OCI Vault; account deletion crypto-shreds the
  DEKs.
- API keys stored hashed; keys are shown once and prefixed for leak scanning.
- Internal control traffic uses mutual TLS with SPIFFE identity; it never
  carries customer data.
- Server binaries run under hardened systemd units on plain Linux hosts; no
  containers.
- Members refuse new writes when a host's clock offset exceeds 500 ms.
- Daily automated restore drills verify backups; failures page an engineer.
- On-call paging fires on customer-visible SLO burn.

## Single-provider risk

All customer data lives in one OCI tenancy plus the Cloudflare edge named
above. This single-provider risk is accepted and documented; we revisit it
at our first $10k MRR.

## Audits and questionnaires

Security questionnaires and audit materials are handled under the Scale
plan. [PLACEHOLDER — audit rights and SOC 2 report access terms pending
O-5 / SOC 2 is post-launch.]

## Breach notification

We will notify affected customers of a confirmed personal data breach within
[TIMEFRAME — placeholder]. [PLACEHOLDER — regulator notification
commitments pending O-5.]
