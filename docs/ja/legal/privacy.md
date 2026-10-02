---
title: Privacy Policy
description: TerraScale Privacy Policy (draft).
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# Privacy Policy

::: warning Draft — not yet in effect
This document is an unpublished draft. It is not yet in effect and does not
govern your use of TerraScale.
:::

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

TerraScale is preparing for public self-service registration. Account and
onboarding information is used to provide the service. [PLACEHOLDER — account
retention and communications choices pending O-5 review.]

## What we store

- **Account identity**: your email, credentials, and organization membership,
  managed by our identity provider.
- **Usage and billing records**: operation units and storage metered per database,
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

A database's bytes at rest exist only in its selected Regions. A placement
lock (`eu-only`, `br-only`, or an explicit region list), once set, cannot be
widened. Reactive delivery may be processed through the listed delivery
provider; in-region edge processing is not promised at launch.

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
revokes sessions and API keys, stops new writes, and begins removal of retained
copies under the applicable deletion workflow. Access remains blocked while
that workflow completes. Billing records are retained as required by law.
[PLACEHOLDER — statutory retention periods, provider retention terms, and
retained-copy completion behavior pending O-4/O-5; do not publish specific
windows yet.]

## Security

We apply encryption, API-key protection, and restricted administrative access
appropriate to the service. See
[Security](./security.md) for the full summary and how to report
vulnerabilities.

## Contact

[PLACEHOLDER — privacy contact and, where required, EU/UK representative,
pending O-5.]

For deletion or privacy requests, use that confirmed contact. Do not send
credentials, API keys, or database contents through the public Discord
channel; see the [Support policy](./support.md).
