---
title: Application security
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Application security

::: warning Draft — not yet in effect
This page is not an effective legal contract or service commitment.
:::

Scope application credentials to the required resources and operations. Keep privileged tokens on the server, use HTTPS and honor expiry and revocation. Do not access data outside resources you own or are authorized to use. No unverified reporting inbox, audit certification or remediation deadline is advertised here.

## Encryption at rest

- **Live databases.** Database files, write-ahead logs, local snapshots and restore scratch files sit on cloud block storage that the provider encrypts with AES-256. This covers every TerraBase node in every region. The provider manages the keys.
- **Backups.** Each backup snapshot and WAL segment is sealed with AES-256-GCM before upload, using a rotating data key that is itself wrapped by a master key. Backups are encrypted again by the object store.
- **Not offered.** Customer-managed keys and per-tenant encryption keys.

See [authentication](/hi/guides/authentication) and [service availability](/hi/getting-started/).

</div>
