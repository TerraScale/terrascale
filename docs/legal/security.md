---
title: Security
description: TerraScale security practices, vulnerability disclosure, and security.txt.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

# Security

::: warning Draft — not yet in effect
This document is an unpublished draft. It is not yet in effect and does not
govern your use of TerraScale.
:::

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).

[PLACEHOLDER — the security inbox, PGP key, and provider confirmations for
OCI and Cloudflare terms are not verified for publication. Do not publish
until O-4/O-5 review is complete.]
:::

## security.txt

At launch, we intend to publish a `security.txt` at
`/.well-known/security.txt` once the owner review is complete:

```text
Contact: mailto:security@terrascale.tech
Expires: [DATE — renew yearly, O-5]
Encryption: [PGP KEY URL — placeholder]
Preferred-Languages: en
Canonical: [PENDING — publish URL after O-4/O-5 review]
Policy: [PENDING — publish URL after O-4/O-5 review]
```

## Vulnerability disclosure

The proposed **security@terrascale.tech** inbox and PGP key are unverified.
Do not send reports or sensitive information there until the channel is
confirmed and published. A future report should include the affected endpoint or component,
reproduction steps, and impact, while omitting credentials and customer
data. We do not offer phone support for reports or otherwise ([support
policy](./support.md)).

We ask for reasonable time to remediate before public disclosure. We do not
promise an acknowledgement or remediation deadline until the owner approves
one: [PLACEHOLDER — acknowledgement and remediation timeframes pending].
[PLACEHOLDER — safe-harbor wording requires legal approval; any permitted
good-faith testing is limited to resources you own or are authorized to test;
no bounty program is approved for launch.]

## Encryption and deletion

The intended service design requires customer data, backups, exports, and
branches to remain encrypted under the database’s residency policy. This
requirement is pending production qualification and owner approval. Authorized
deletion is intended to revoke access and stop further billable usage while
retained copies are removed. Physical completion timing requires verified
retention behavior and owner approval; see the [deletion and billing
policy](/pricing/). This draft does not promise a fixed key-rotation interval
or unverified completion deadline.

A SOC 2 report and a paid external penetration test are not available at
launch; any future work requires separate qualification and owner approval.
