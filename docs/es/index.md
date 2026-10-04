---
layout: home

hero:
  name: TerraScale
  text: Multi-region document database
  tagline: Selected Regions, reactive live queries, and read-only SQL. Metered in operation units and storage across selected regions, with hard plan allowances.
  actions:
    - theme: brand
      text: Get started
      link: /es/getting-started/
    - theme: alt
      text: Pricing & limits
      link: /es/pricing/
    - theme: alt
      text: Concepts
      link: /es/concepts/replication

features:
  - icon: ⚙️
    title: Live results across selected Regions
    details: Subscribe to authorized results and receive changes as documents are written. Writes count once per selected Region and storage counts in every selected Region before included allowances.
  - icon: 📝
    title: SQL you already know
    details: "Every collection is a SQL table: typed columns from your schema, JSON paths into the document body, full-text search. The same query ends in .subscribe() for live results or .execute() for one shot."
  - icon: 🌍
    title: Active-active multi-region
    details: Choose placement in GRU (São Paulo), SFO (San Jose), and FRA (Frankfurt). Causal sessions and deterministic conflict resolution have explicit limits; global uniqueness and linearizable reads are not promised.
  - icon: 💰
    title: Operation units and storage, nothing else
    details: One operation unit = each started 4 KiB read or written; writes count once per selected region and reads only in the serving region. Storage counts once in every selected region. Plans use hard allowances with no paid overages.
  - icon: 🌿
    title: Branches, PITR, per-document history
    details: Keep retained document history within plan limits. Recovery and branching preserve placement and access scope; their real-service workflows require qualification.
  - icon: 🧩
    title: A database per customer
    details: "Separate customer databases can scope access, usage, recovery, and residency. Capacity and idle-cost claims require measured evidence."
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

TerraScale is preparing for public self-service launch. Production qualification
and final pricing approval remain incomplete; these pages describe the intended
service and identify unavailable workflows.
