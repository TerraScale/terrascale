---
title: Live updates and checkpoints
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Live updates and checkpoints

The published JavaScript SDK includes collection `shape` reads for snapshots and change-log chunks. That surface is separate from the old SQL/reactive-view client and from proof of a deployed live-update journey.

## Current shape surface

A shape bootstrap starts with an offset of `-1`. Responses carry a handle, next offset and continuation cursor; preserve those fields unchanged for the next read. A `must_refetch` response means the application must discard its previous shape state and obtain a fresh authorized snapshot. Do not interpret a handle or cursor as a replication vector.

The supported request and response contract must match the serving release. A collection query page is a point-in-time response, not a subscription. The CRUD quickstart does not qualify snapshot resume, reconnect, permission changes, slow consumers or WebSocket delivery.

## Compatibility boundary

Older `TerraScaleSyncV1Client`, shared reactive views and SQL subscription examples target a different SDK contract and an unpublished pin. They are retained only as local compatibility fixtures. Do not copy them into a new application using the current root entrypoint.

Keep durable writes on the authorized server path. A reversible optimistic UI does not prove a write committed or create an offline mutation contract. Confirm real-service live-update availability before implementing a browser integration.

</div>
