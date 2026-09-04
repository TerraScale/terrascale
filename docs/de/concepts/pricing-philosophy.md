---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Pricing philosophy

**Writes and storage are the only billed dimensions.** Everything else —
reads, queries, egress, CDN delivery, fan-out, concurrent subscribers,
WebSocket connections, webhook delivery, catch-up and replay, TTL expiry — is
free, unlimited, and unbilled. Guards on those free dimensions exist to stop
abuse, not to bill you (see [pricing & limits](/de/pricing/)).

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
counts **+1 write per region**. That is the entire metering rule for writes.

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

## Why we can price this way

The engine is built so free things are actually cheap to give away:

- Reads are zero-parse: documents are canonical bytes spliced into responses.
- Live delivery is an immutable HTTP log the CDN caches and collapses; a
  cache hit runs no metered compute at the edge and no database work at the
  origin. One million readers of one shared view cost the same as one.
- Storage is mainline SQLite — one file per database, idle files closed, so
  a database at rest costs almost nothing.

Every price must clear a measured unit-economics bar before it is published:
each billed line shows the margin at list price on measured cost, and prices
only ever go down. Engine usage facts — not sampling, not estimates — are the
source of truth on your invoice, and the dashboard shows per-query cost
attribution down to individual queries.
