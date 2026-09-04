---
title: Security
description: TerraScale security practices, vulnerability disclosure, and security.txt.
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# Security

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).

[PLACEHOLDER — the security inbox, PGP key, and provider confirmations for
OCI and Cloudflare terms are not verified for publication. Do not publish
until O-4/O-5 review is complete.]
:::

## security.txt

At launch, we intend to publish a `security.txt` at
[`/.well-known/security.txt`](https://docs.terrascale.tech/.well-known/security.txt):

```text
Contact: mailto:security@terrascale.tech
Expires: [DATE — renew yearly, O-5]
Encryption: [PGP KEY URL — placeholder]
Preferred-Languages: en
Canonical: https://docs.terrascale.tech/.well-known/security.txt
Policy: https://docs.terrascale.tech/legal/security
```

## Vulnerability disclosure

Report vulnerabilities to **security@terrascale.tech** (draft address;
encrypted with the PGP key above once published — [PLACEHOLDER — key and
inbox monitoring pending]). Include the affected endpoint or component,
reproduction steps, and impact, while omitting credentials and customer
data. We do not offer phone support for reports or otherwise ([support
policy](./support.md)).

We ask for reasonable time to remediate before public disclosure. We do not
promise an acknowledgement or remediation deadline until the owner approves
one: [PLACEHOLDER — acknowledgement and remediation timeframes pending].
[PLACEHOLDER — safe-harbor wording requires legal approval; any permitted
good-faith testing is limited to resources you own or are authorized to test;
no bounty program is approved for the beta.]

## Key hierarchy

Every database has its own data encryption key (DEK), wrapped by a
per-region key encryption key (KEK) in OCI Vault. SQLite files, WAL,
backups, exports, and branches are encrypted under the DEK. Deleting an
account destroys its DEKs — crypto-shred — making the data unreadable.
The capability-signing key rotates every 24 hours. Administrative access
("crown jewels") requires hardware two-factor authentication.

A SOC 2 report and a paid external penetration test are planned
post-first-customer.
