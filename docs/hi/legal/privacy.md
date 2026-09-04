---
title: Privacy Policy
description: TerraScale Privacy Policy (draft).
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Privacy Policy

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).

[PLACEHOLDER — written confirmation of OCI and Cloudflare processing,
residency, and CDN/cache terms, and of the Stripe Tax account/configuration,
is not on file (O-4). Do not publish this draft until confirmed and reviewed
by the owner.]
:::

This Privacy Policy explains how [LEGAL ENTITY NAME — O-5],
[JURISDICTION — O-5], [ADDRESS] handles personal data. Effective
[EFFECTIVE DATE].

TerraScale is in a waitlist beta. A waitlist submission may include your
email, organization, and invitation or onboarding details so we can operate
the waitlist and send service or launch updates. [PLACEHOLDER — waitlist
retention and communications choices pending O-5 review.]

## What we store

- **Account identity**: your email, credentials, and organization membership,
  managed by our identity provider and stored in our control-plane database.
- **Usage and billing records**: writes and storage metered per database,
  invoices and payments (intended to be processed by Stripe; Stripe Tax is
  intended from day one to calculate applicable taxes). [PLACEHOLDER —
  Stripe Tax configuration and tax-treatment language pending O-4/O-5.]
- **Audit logs**: administrative actions and access in our systems.
- **Your application data**: whatever you store in your databases. It is
  yours; we process it only to provide the service.

We do not sell personal data. We run no advertising or tracking on our
sites.

## Sub-processors

| Sub-processor | Role |
| --- | --- |
| Oracle Cloud Infrastructure (OCI) | Hosting, storage, backups, and email delivery for the service |
| Cloudflare | DNS, TLS, WAF, and CDN delivery of your authorized reactive data; edge decryption occurs at the point of presence nearest the reader, worldwide |
| Stripe | Intended billing, invoicing, and Stripe Tax processing; database content is not intended to be sent for this purpose |
| [IDENTITY PROVIDER — placeholder] | Account authentication and identity data [provider, role, locations, and terms pending confirmation] |

This is the intended launch list. [PLACEHOLDER — OCI and Cloudflare written
provider confirmations, Cloudflare cache terms, and Stripe Tax account and
configuration confirmation are pending O-4. The identity-provider entry must
be completed or removed before publication.] No other customer-data
subprocessor is approved for this draft.

## Residency

A database's bytes at rest exist only in its selected Regions — enforced at
placement, replication, backup, restore, export, and branching. A placement
lock (`eu-only`, `br-only`, or an explicit region list), once set, cannot be
widened. Cloudflare's edge decrypts reactive traffic at the PoP nearest the
reader; we do not promise in-region edge processing at launch.

## Cookies

Our dashboard sets authentication cookies only. There are no advertising or
tracking cookies.

## Choices and rights

You can unsubscribe from non-essential launch updates, and you can request
access, correction, export, or deletion of account information through the
confirmed privacy contact. Rights and exceptions depend on the law that
applies to you. [PLACEHOLDER — legal bases, jurisdiction-specific rights,
identity-verification steps, response periods, and international-transfer
mechanisms pending O-5 review.]

## Email

We send email from the `terrascale.tech` domain with SPF, DKIM, and DMARC
configured.

## Retention and deletion

Account data is kept while your account is active. Deleting your account
revokes sessions and API keys, stops new writes, requests purge of all
Cloudflare cache entries tagged for the database, and destroys the database's
per-database encryption keys (crypto-shred), rendering database files, WAL,
backups, exports, and branches unreadable. Cache purge is a separate
defense-in-depth step; revoked capabilities prevent new authorized reads
while the provider processes the purge. Backups age out under lifecycle
rules. Billing records are retained as required by law. [PLACEHOLDER —
statutory retention periods, OCI backup lifecycle, and Cloudflare purge
completion behavior pending O-4/O-5; do not publish specific windows yet.]

## Security

Databases are encrypted with per-database keys wrapped by regional key
encryption keys in OCI Vault; API keys are stored hashed; administrative
access requires hardware two-factor authentication. See
[Security](./security.md) for the full summary and how to report
vulnerabilities.

## Contact

[PLACEHOLDER — privacy contact and, where required, EU/UK representative,
pending O-5.]

For deletion or privacy requests, use that confirmed contact. Do not send
credentials, API keys, or database contents through the public Discord
channel; see the [Support policy](./support.md).
