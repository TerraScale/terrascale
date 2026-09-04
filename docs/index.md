---
layout: home

hero:
  name: TerraScale
  text: Multi-region document database
  tagline: Leaderless CRDT replication, CDN-backed reactive live queries, and SQL you already know — on one radically simple price. Pay for writes and storage; reads, queries, egress, and delivery are free.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started/
    - theme: alt
      text: Pricing & limits
      link: /pricing/
    - theme: alt
      text: Concepts
      link: /concepts/engines

features:
  - icon: ⚙️
    title: Two engines, one product
    details: A replication engine (leaderless CRDT across regions, RF=2 inside a region) and a sync engine (incremental result views encoded once into an immutable HTTP log the CDN serves and collapses). Replicated writes cost one write per region — shown up front.
  - icon: 📝
    title: SQL you already know
    details: "Every collection is a SQL table: typed columns from your schema, JSON paths into the document body, FTS5 full-text search. The same query ends in .subscribe() for live results or .execute() for one shot."
  - icon: 🌍
    title: Active-active multi-region
    details: Write in GRU, SFO, and FRA at the same time. Single-digit-ms local reads, causal consistency by default, deterministic conflict resolution with per-document history that explains every merge.
  - icon: 💰
    title: Writes and storage, nothing else
    details: One write = one create/update/patch/delete of ≤ 4 KiB. Storage = logical bytes × regions. Reads, queries, egress, CDN delivery, fan-out, subscribers, webhooks, catch-up and replay are free and unlimited — bounded only by unbilled abuse guards.
  - icon: 🌿
    title: Branches, PITR, per-document history
    details: Databases and branches create in under 5 seconds. Restore to a new database, branch from any point in time, and see "why this version won" for every conflict.
  - icon: 🧩
    title: A database per customer
    details: "Thousands of small databases per project are routine and near-free when idle. Tenant = database: scoped keys, per-tenant usage, per-tenant branching, and placement locks that are never widened."
---
