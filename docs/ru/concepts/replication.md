---
title: Replication and durability
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Replication and durability

Read the durability reported by the serving API literally. The current Documents examples do not establish leaderless operation, global serializability or a multi-region production guarantee.

## Current application contract

Document writes return an opaque version, an offset and a durability label. The candidate's `local_durable` label is an accepting-service acknowledgement. It is not proof that every selected region has persisted the write. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not establish globally unique application values or cross-region linearizability.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/ru/guides/errors) describes reconciliation.

## Multi-region product requirements

The intended service keeps data within the selected placement and converges authorized replicas without widening residency. Fault recovery, causal sessions, conflict resolution and cross-region durability require independent real-service evidence. The retained isolated CRUD receipt does not qualify them.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. Those names are not selectable write modes in the documented current Documents SDK example. Do not assume a current SDK can request them or provides their historical guarantees.

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/ru/guides/sdks), [live-update compatibility](/ru/concepts/sync) and [pricing](/ru/pricing/) for their separate boundaries.

</div>
