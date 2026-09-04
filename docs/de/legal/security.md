---
title: Security
description: TerraScale security practices, vulnerability disclosure, and security.txt.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Security

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).
:::

## security.txt

We publish a `security.txt` at
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

Report vulnerabilities to **security@terrascale.tech** (encrypted with the
PGP key above once published — [PLACEHOLDER — key pending]). Include: the
affected endpoint or component, reproduction steps, and impact. We do not
offer phone support for reports or otherwise ([support policy](./support.md)).

We ask for reasonable time to remediate before public disclosure, and we
commit to acknowledging reports within [PLACEHOLDER — acknowledgement
timeframe]. [PLACEHOLDER — safe-harbor wording: good-faith testing against
your own resources, no bounty program at launch.]

## Key hierarchy

Every database has its own data encryption key (DEK), wrapped by a
per-region key encryption key (KEK) in OCI Vault. SQLite files, WAL,
backups, exports, and branches are encrypted under the DEK. Deleting an
account destroys its DEKs — crypto-shred — making the data unreadable.
The capability-signing key rotates every 24 hours. Administrative access
("crown jewels") requires hardware two-factor authentication.

A SOC 2 report and a paid external penetration test are planned
post-first-customer.
