---
---
<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# Replication and durability

> Pre-launch: these describe required service guarantees. Production fault and
> recovery qualification remains incomplete.

A database accepts reads and writes in its selected Regions. Placement is
explicit and immutable: selecting additional Regions changes both residency
and usage accounting. Copies across Regions converge with causal replication
and deterministic conflict resolution. This does not provide linearizable
reads or global uniqueness.

## Durability choices

- **RegionalDurable** acknowledges a write after it is durable on both copies
  in the accepting Region.
- **GlobalDurable** requires durability in every selected Region, including
  the write’s causal dependencies. An unavailable required Region prevents
  this acknowledgement; the service must not silently weaken durability.
  At least two selected Regions must be active; a single-Region database
  cannot request this acknowledgement.

A timeout may leave a write’s outcome uncertain. Reuse its idempotency identity
and follow the SDK’s retry guidance; creating a new operation can duplicate it.

## Sessions and conflicts

Session guarantees include read-your-writes and monotonic reads. Keep the
opaque session token returned by the API and pass it on subsequent requests.
A Region that cannot satisfy the requested session must return a useful
restriction or retry response rather than silently return stale data.

Concurrent writes across Regions can conflict. Deterministic resolution makes
copies converge; retained history provides conflict information within the
configured retention period. Do not treat a local uniqueness or conditional
write check as a global constraint. Applications needing global uniqueness
must use a separately qualified authority for that decision.

During an outage, admitted operations may continue only where the requested
placement, session, authorization, and durability guarantees can be satisfied.
Recovery does not widen residency or invent a successful acknowledgement.

## Usage

Multiply each write’s 4 KiB units by selected Regions before consuming the
organization’s shared database and branch allowances. Logical storage also
counts selected Regions over time. Reads and replication delivery create no
separate charge; see [pricing and limits](/ja/pricing/).
