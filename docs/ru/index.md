---
layout: home

hero:
  name: TerraScale
  text: Multi-region document database
  tagline: Selected Regions, reactive live queries, and read-only SQL. Pay for writes and storage; reads, queries, egress, and delivery are free.
  actions:
    - theme: brand
      text: Get started
      link: /ru/getting-started/
    - theme: alt
      text: Pricing & limits
      link: /ru/pricing/
    - theme: alt
      text: Concepts
      link: /ru/concepts/replication

features:
  - icon: ⚙️
    title: Live results across selected Regions
    details: Subscribe to authorized results and receive changes as documents are written. Selected Regions multiply write units and logical storage before included allowances.
  - icon: 📝
    title: SQL you already know
    details: "Every collection is a SQL table: typed columns from your schema, JSON paths into the document body, full-text search. The same query ends in .subscribe() for live results or .execute() for one shot."
  - icon: 🌍
    title: Active-active multi-region
    details: Choose placement in GRU, SFO, and FRA. Causal sessions and deterministic conflict resolution have explicit limits; global uniqueness and linearizable reads are not promised.
  - icon: 💰
    title: Writes and storage, nothing else
    details: One write = one create/update/patch/delete of ≤ 4 KiB. Storage = logical bytes × regions. Reads, queries, egress, CDN delivery, fan-out, subscribers, webhooks, catch-up and replay are unbilled within disclosed safety guards.
  - icon: 🌿
    title: Branches, PITR, per-document history
    details: Keep retained document history within plan limits. Recovery and branching preserve placement and access scope; their real-service workflows require qualification.
  - icon: 🧩
    title: A database per customer
    details: "Separate customer databases can scope access, usage, recovery, and residency. Capacity and idle-cost claims require measured evidence."
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

TerraScale is preparing for public self-service launch. Production qualification
and final pricing approval remain incomplete; these pages describe the intended
service and identify unavailable workflows.
