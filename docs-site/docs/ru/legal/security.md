---
title: Application security
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

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

See [authentication](/ru/guides/authentication) and [service availability](/ru/getting-started/).

</div>
