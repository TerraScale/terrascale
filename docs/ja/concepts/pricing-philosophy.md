---
---
<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# Pricing philosophy

**Writes and storage are the only billed dimensions.** Everything else —
reads, queries, egress, CDN delivery, fan-out, concurrent subscribers,
WebSocket connections, webhook delivery, catch-up and replay, TTL expiry — is
free and unbilled within disclosed safety limits. Guards on those free dimensions exist to stop
abuse, not to bill you (see [pricing & limits](/ja/pricing/)).

Stated precisely, because honesty is the brand:

- **Delivery is free. Infrastructure is not.** We pay for the edge, the
  regions, and the hardware; the architecture is built so that cost follows
  writes and stored bytes, and those two prices carry it.
- **Replicated writes cost one write per region.** We never say "no
  per-region cost" — multi-region durability is real work and it is priced as
  exactly what it is, shown up front when you select regions.

## The write

One write = one create, update, patch, or delete of **≤ 4 KiB**, counted as
`ceil(bytes / 4 KiB)`. Replicating that write to another selected region
multiplies its 4 KiB units by the number of selected Regions before allowances. That is the entire metering rule for writes.

A write is billed **once** regardless of how many shared views it affects or
how many clients are attached to them — authorization-equivalent subscribers
share one view, one delta computation, one log append. Fan-out, catch-up, and
replay are free. If your bill went up because your app got popular with
readers, something would be wrong with our architecture, not with your app.

## The gigabyte

Storage is **logical bytes × regions**: the canonical bytes of your current
documents plus retained history, TTL-aware, measured in byte-seconds, billed
at $/GB-month per region. Indexes, WAL, and backups are **not billed** — you
should never be taxed for our performance work.

## Prices require evidence

Before publication, each billed line must sustain at least 50% measured
margin, and unbilled dimensions must have measured worst cases under their
guards. The draft rates need owner approval of that evidence and the final
price sheet. Customer usage facts must reconcile with invoices.

A stored card does not authorize excess usage. Databases and branches share
normal organization allowances; there is no additional branch allowance.
Review the [billing policy](/ja/pricing/) for explicit consent, spending caps,
continuing storage costs, and retention policies.
