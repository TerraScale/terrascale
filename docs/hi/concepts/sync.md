---
title: Live updates and checkpoints
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Live updates and checkpoints

The JavaScript SDK (`@terrascale/sdk` 0.0.7 or newer) subscribes to a filtered collection shape with `collection.subscribeShape()`. It delivers the initial result, then every matching change, and keeps running across network drops and token renewal. See [Realtime changes](/hi/guides/realtime#live-queries-in-javascript).

## Current shape surface

A shape bootstrap starts with an offset of `-1`. Responses carry a handle, next offset and continuation cursor; preserve those fields unchanged for the next read. A `must_refetch` response means the application must discard its previous shape state and obtain a fresh authorized snapshot. Do not interpret a handle or cursor as a replication vector.

A collection query page is a point-in-time response, not a subscription. The SDK subscription handles these cases:

- **Network drop:** it keeps its documents and retries the same position with backoff, then delivers what changed meanwhile.
- **Token renewal:** with a token function, each long-poll uses the current token. A `401` or `403` stops the subscription.
- **`must_refetch`:** it discards its state and loads a fresh snapshot. The server answers this after a permission change, a restore or a dropped collection.
- **Restart:** with a read replica, it shows the last saved documents at once and catches up from the saved position.

Delivery uses HTTP long-polls. There is no WebSocket transport.

## Compatibility boundary

Older `TerraScaleSyncV1Client`, shared reactive views and SQL subscription examples target a different SDK contract and an unpublished pin. They are retained only as local compatibility fixtures. Do not copy them into a new application using the current root entrypoint.

Keep durable writes on the authorized server path. A reversible optimistic UI does not prove a write committed or create an offline mutation contract. A read replica serves reads while offline; it never queues writes. For live updates, use the changes feed or a filtered shape as described in [Realtime changes](/hi/guides/realtime). In a browser, read the feed with `fetch` and an `Authorization` header (`EventSource` cannot send one), using a short-lived token you renew before it expires.

</div>
