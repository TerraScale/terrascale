---
---
<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

# Sync model

Live queries are the product's heartbeat. The sync engine maintains the
complete result of a subscribable query from the replication engine's durable
publication journal and delivers changes over plain HTTP that a CDN can cache
and collapse — by design, with no server-side code on the hot path.

## Two modes over one contract

- **Server live query** — the full result of a live-subset SQL query, with
  ordering and limits, maintained incrementally. Use it for bounded results
  like "top 20 posts by this author".
- **Collection sync** — an authorized outer subset that a local store (the
  TanStack DB adapter) narrows with client-side live queries, joins, and
  grouping. This is the recommended pattern: it keeps distinct-view counts
  low, which is what actually scales.

Both support checkpoint/resume and typed resets. There are no offline writes
or full client replicas at launch.

## Shared views: readers share work

When clients subscribe, their query is planned, compiled with the access
rules, and folded into a **canonical shared view key**: database incarnation,
plan, parameters, projection, schema and index versions, rule revision,
entitlement epoch, and region. Authorization-equivalent clients attach to one
view, one revision line, and one bounded immutable delta log — no matter how
many of them there are.

The subscription returns a view id, snapshot chunk URLs, the log URL, a
checkpoint, and a short-lived **view capability** that travels in a request
header (never in the URL or cache key), so every equivalent reader hits the
same cache entry.

## Deltas, not re-reads

On every commit, the interval-tree index yields the affected views from
extracted values already in hand. Each view receives the row's Z-set delta
through its compiled filter — linear, no query state, no re-execution. Once
per **100 ms window**, one delta per affected view is encoded to MessagePack
and appended to the immutable log. Writes inside the window coalesce; reader
request rate is bounded independently of write rate.

## Delivery: CDN-first HTTP

`.subscribe()` defaults to bounded HTTP long-polling (25 s window) on
`sync.api.terrascale.tech`:

- A Cloudflare **Snippet** — free, unlimited invocations — verifies the
  capability, strips it, and fetches the canonical URL through the cache.
  **No Worker runs on this path.**
- A data-bearing response for an offset is **frozen, ETagged, and immutable**
  for the retention window; every reader at that offset gets the same bytes.
  Cache hits cost zero metered compute and zero bandwidth at the edge.
- Simultaneous misses collapse at the PoP; tiered cache bounds origin fills.

WebSocket is an opt-in transport override for latency-critical clients — it
cannot be cached or collapsed, so it is not the default. SSE is not offered
for the same reason.

## Checkpoints and resets

A checkpoint binds view identity, generation, incarnation, region, and the
retained journal boundary. Resume within retention replays retained
revisions; anything that changed (schema, rules, entitlements, branch
rebase, retention overrun) returns a **typed reset reason** and a fresh
authorized snapshot. One revision is one journal cut: two different views
promise no mutual consistency — need a consistent cut across data? Use one
collection-sync view.

## What it costs

Nothing beyond writes and storage. A write is billed **once**, no matter how
many shared views it affects or how many clients are attached. Subscribers per
view are unlimited on every plan; the only guard is a per-database
distinct-view cap ([pricing](/es/pricing/)) — an abuse limit, not a price.
