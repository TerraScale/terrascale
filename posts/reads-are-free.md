# Reads are free: how

> DRAFT — launch post outline + draft paragraphs. Not published.

## Outline

- The pricing model (D-16): writes and storage are the only billed dimensions; reads, queries, egress, CDN delivery, fan-out, subscribers, WebSocket connections, webhook delivery, catch-up/replay, TTL expiry are free.
- A claim like that is only honest if the architecture amortizes delivery instead of metering it — this post is the mechanism.
- The reactive path (D-46): one canonical shared view per authorization-equivalent group; encode-once immutable delta log; 100 ms cadence; MessagePack.
- CDN-first delivery (D-55/D-14): HTTP long-polling on `sync.api.terrascale.tech`; a Cloudflare Snippet (free, unlimited) verifies the capability header and fetches through the cache; no Worker ever runs on the path.
- Why a cache hit costs nothing metered: Workers bill per request including hits ($0.30/M) — which is why there is no Worker there; a hit is edge bandwidth ($0) plus a Snippet invocation ($0).
- Readers share bytes: a data-bearing response for an offset is frozen, ETagged, immutable, and identical for every authorization-equivalent reader; one million readers receive the same bytes.
- A write is billed once regardless of how many views it affects or how many clients are attached (D-22).
- The honest fine print: infrastructure is not free — it is amortized by writes and bounded by abuse guards (D-64); replicated writes cost one write per region (D-16).

## Draft

TerraScale bills exactly two things: writes and storage (D-16). Reads, queries, egress, CDN delivery, fan-out, concurrent subscribers, WebSocket connections, webhook delivery, catch-up and replay, TTL expiry — all free, all unlimited. Pricing pages say things like this all the time and mean "free until it isn't." We mean it structurally, and the reason is that the delivery path for reactive queries was designed so that serving one more reader costs us nothing metered.

Start with the server side (D-46). When clients subscribe to a query, authorization-equivalent subscribers attach to one canonical shared view — one plan, one incremental state, one revision line. On every commit, the affected views each get one Z-set delta, and once per 100 ms cadence window that delta is encoded to MessagePack and appended to an immutable log. The encoding happens once per view per window, not once per reader. Ten subscribers or ten thousand: same computation, same bytes.

Those bytes are then delivered by Cloudflare's CDN, not by our servers (D-55). Subscriptions use bounded HTTP long-polling on `sync.api.terrascale.tech`. A Cloudflare Snippet — free, unmetered — verifies the view capability header (an HMAC binding view, generation, authorization scope, and expiry), strips it, and performs the canonical fetch through the zone cache. There is no Worker on this path, and that is the whole trick: Cloudflare bills every Worker request, including cache hits, at $0.30 per million — a price that is flatly incompatible with free fan-out. A Snippet invocation costs nothing, edge bandwidth costs nothing, so a cache hit costs us nothing metered.

The cache works because the responses were built to be cached. A data-bearing response for a given offset is frozen the moment it is produced: deterministic boundaries, byte-identical MessagePack, a strong ETag, immutable through the retention window. Every reader of that view asks for the same URL with the same offset and receives the same bytes. Simultaneous misses collapse to one origin fetch per PoP; Tiered Cache bounds origin fills across PoPs. Readers don't just get the same answer — they get literally the same response object, served from the edge.

The billing consequence (D-22): a write is billed once, regardless of how many shared views it affects or how many clients are attached to them. Fan-out is the CDN's problem, and the CDN is priced at zero for this traffic shape. The guard against abuse is not a price — it is the per-database distinct-view cap (D-64), an enforced limit that returns a typed error with the fix, never an invoice.

Now the honest fine print, because honesty is the brand and this post is on the record. Infrastructure is not free; it is amortized. Origin compute, replication, and storage are paid for by the write and storage lines — that is what makes the model work rather than merely sound good. And replication is billed exactly as it costs us: a write replicated to another region counts one write per region, and we show that up front rather than hiding it in the word "global." Reads are free because we engineered the read path until that sentence was true.
