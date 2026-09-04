---
---
<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

# Reads are free: how (draft)

> DRAFT. This launch post is not published. The pricing rule is fixed; the
> edge and unit-economics measurements below must land before publication.

## Outline

- The pricing model (D-16): writes and storage are the only billed dimensions; reads, queries, egress, CDN delivery, fan-out, and subscribers are free and unbilled.
- A claim like that is only honest if the architecture amortizes delivery instead of metering it. This post explains the mechanism.
- The reactive path (D-46): one canonical shared view per authorization-equivalent group; encode-once immutable delta log; 100 ms cadence; MessagePack.
- CDN-first delivery (D-55/D-14): HTTP long-polling on `sync.api.terrascale.tech`; a Cloudflare Snippet verifies the capability header and fetches through the cache; no Worker runs on the path.
- Why a cache hit runs no metered compute: Workers bill per request including hits ($0.30/M), which is why there is no Worker there; the Snippet gate and cache path are the selected design.
- Readers share bytes: a data-bearing response for an offset is frozen, ETagged, immutable, and identical for every authorization-equivalent reader. The one-million-reader test remains a launch measurement, not a result.
- A write is billed once regardless of how many views it affects or how many clients are attached (D-22).
- The honest fine print: infrastructure is not free. It is amortized by writes and bounded by abuse guards (D-64); replicated writes cost one write per region (D-16).

## Draft

TerraScale bills exactly two things: writes and storage (D-16). Reads, queries, egress, CDN delivery, fan-out, concurrent subscribers, WebSocket connections, webhook delivery, catch-up and replay, and TTL expiry are free and unbilled. Pricing pages say things like this all the time and mean "free until it isn't." Here the billing rule follows from the delivery design. Serving another reader does not add a metered Worker request.

Start with the server side (D-46). When clients subscribe to a query, authorization-equivalent subscribers attach to one canonical shared view: one plan, one incremental state, one revision line. On every commit, the affected views each get one Z-set delta. Once per 100 ms cadence window, that delta is encoded to MessagePack and appended to an immutable log. The encoding happens once per view per window, not once per reader. Ten subscribers or ten thousand use the same computation and bytes.

Those bytes are then delivered by Cloudflare's CDN, not by our servers (D-55). Subscriptions use bounded HTTP long-polling on `sync.api.terrascale.tech`. A Cloudflare Snippet verifies the view capability header, an HMAC binding the view, generation, authorization scope, and expiry. It strips that header and performs the canonical fetch through the zone cache. There is no Worker on this path. Cloudflare bills every Worker request, including cache hits, at $0.30 per million, so a Worker would add a metered request to every reader. The Snippet and cache path avoid that request. Customer delivery remains unbilled under D-16 and is bounded by the documented abuse guards.

The cache works because the responses were built to be cached. A data-bearing response for a given offset is frozen when it is produced. It has deterministic boundaries, byte-identical MessagePack, a strong ETag, and immutability through the retention window. Every reader of that view asks for the same URL with the same offset and receives the same bytes. Simultaneous misses collapse to one origin fetch per PoP. Tiered Cache bounds origin fills across PoPs. Readers get the same response object from the edge.

The billing consequence (D-22) is simple. A write is billed once, regardless of how many shared views it affects or how many clients attach to them. The per-database distinct-view cap (D-64) is an enforced abuse limit. It returns a typed error with the fix. It does not create an invoice.

Here is the fine print. Infrastructure is not free. Origin compute, replication, and storage are funded by the write and storage lines. Replication is billed exactly as specified: a write replicated to another region counts as one write per region, and the pricing page shows that up front. Reads are free because the product's billing rule and delivery path were designed together. The one-million-reader result remains pending the production test in X-5.
