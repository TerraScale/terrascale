---
title: Privacy Policy
description: TerraScale Privacy Policy (draft).
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

# Privacy Policy

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).
:::

This Privacy Policy explains how [LEGAL ENTITY NAME — O-5],
[JURISDICTION — O-5], [ADDRESS] handles personal data. Effective
[EFFECTIVE DATE].

## What we store

- **Account identity**: your email, credentials, and organization membership,
  managed by our identity provider and stored in our control-plane database.
- **Usage and billing records**: writes and storage metered per database,
  invoices and payments (processed by Stripe; Stripe Tax applies from day
  one so every invoice is tax-correct).
- **Audit logs**: administrative actions and access in our systems.
- **Your application data**: whatever you store in your databases. It is
  yours; we process it only to provide the service.

We do not sell personal data. We run no advertising or tracking on our
sites.

## Sub-processors

| Sub-processor | Role |
| --- | --- |
| Oracle Cloud Infrastructure (OCI) | Hosting, storage, backups, email delivery |
| Cloudflare | DNS, TLS, WAF, and CDN delivery of your authorized reactive data; edge decryption occurs at the point of presence nearest the reader, worldwide, under Cloudflare's DPA |
| Stripe | Billing and tax processing |

No other cloud or PaaS is used.

## Residency

A database's bytes at rest exist only in its selected Regions — enforced at
placement, replication, backup, restore, export, and branching. A placement
lock (`eu-only`, `br-only`, or an explicit region list), once set, cannot be
widened. Cloudflare's edge decrypts reactive traffic at the PoP nearest the
reader; we do not promise in-region edge processing at launch.

## Cookies

Our dashboard sets authentication cookies only. There are no advertising or
tracking cookies.

## Email

We send email from the `terrascale.tech` domain with SPF, DKIM, and DMARC
configured.

## Retention and deletion

Account data is kept while your account is active. Deleting your account
destroys your databases' per-database encryption keys (crypto-shred),
rendering the data unreadable; backups age out under lifecycle rules.
Billing records are retained as required by law. [PLACEHOLDER — statutory
retention periods pending O-5.]

## Security

Databases are encrypted with per-database keys wrapped by regional key
encryption keys in OCI Vault; API keys are stored hashed; administrative
access requires hardware two-factor authentication. See
[Security](./security.md) for the full summary and how to report
vulnerabilities.

## Contact

[PLACEHOLDER — privacy contact and, where required, EU/UK representative,
pending O-5.]
