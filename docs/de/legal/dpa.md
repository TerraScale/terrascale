---
title: Data Processing Addendum
description: TerraScale Data Processing Addendum (draft).
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Data Processing Addendum

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).

[PLACEHOLDER — OCI and Cloudflare written provider confirmations (including
processing terms, locations, and CDN/cache terms) and the Stripe Tax
configuration confirmation are not on file (O-4). Do not publish until these
items and the controller's legal review are complete.]
:::

This Data Processing Addendum ("DPA") is between [LEGAL ENTITY NAME — O-5],
[JURISDICTION — O-5], [ADDRESS] ("Processor") and the customer ("Controller").
Effective [EFFECTIVE DATE].

## Scope

Customers store application data in TerraScale databases. For that content,
we act as processor; the customer is the controller. Account and billing
data are processed under our [Privacy Policy](./privacy.md). Stripe Tax is
intended to be enabled from day one for tax calculation; [PLACEHOLDER —
Stripe Tax account/configuration and provider confirmation pending O-4.]

## Sub-processors

| Sub-processor | Processing | Locations |
| --- | --- | --- |
| Oracle Cloud Infrastructure | Compute hosts, block storage, object storage (backups), and email delivery | Tenancy in [JURISDICTION(S)] with GRU home region; [written residency/processing confirmation pending O-4] |
| Cloudflare | Edge network: DNS, TLS termination, WAF, CDN cache and delivery of authorized reactive data; edge decryption at the PoP nearest the reader worldwide | Global edge network; [written CDN/cache terms and processing confirmation pending O-4] |
| Stripe | Billing, invoicing, and intended Stripe Tax processing; no customer database content | [PLACEHOLDER — Stripe account/configuration and processing locations pending confirmation] |
| [IDENTITY PROVIDER — placeholder] | Account authentication and identity data | [PLACEHOLDER — provider, role, locations, and written terms pending confirmation] |

This is the intended launch list. [PLACEHOLDER — the identity-provider name,
role, locations, and written terms must be confirmed or removed before
publication.] No other customer-data subprocessor is approved for this draft.
The reactive delivery contract is plain HTTP caching semantics, so the CDN is
replaceable without changing the intended security posture; no replacement
provider is authorized by this draft.

We will use each subprocessor only on documented instructions and will bind it
to confidentiality and appropriate security obligations. [PLACEHOLDER —
subprocessor notice, objection, and replacement terms pending O-5 legal
review.]

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
- Restore drills and incident paging are intended operational controls; their
  launch availability remains awaiting qualification.

On account deletion, we revoke access, stop new writes, request purge of
Cloudflare cache entries carrying the database's cache tags, and then
destroy the database DEKs (crypto-shred). Revocation makes capabilities
invalid while the CDN processes the purge. [PLACEHOLDER — exact purge
completion and OCI backup-retention windows pending written provider
confirmation (O-4).]

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
