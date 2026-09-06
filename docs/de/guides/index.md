---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Guides

Task-focused guides. During pre-launch this section is an index of what ships
at public launch; published runnable snippets run against a real branch
pre-filled with your key. Management fixtures whose producer contracts are not
published are retained as non-runnable references.

## Data

- **Live queries in React** — `useQuery`, optimistic updates, typed resets.
- **Collection sync with TanStack DB** — the recommended local-first pattern;
  keeps distinct-view counts low.
- **Full-text search** — FTS5 `MATCH`, auto-indexed.
- **TTL** — expiry is free; TTL removal pushes are free.

## Operations

- **Branches** — `branch-per-developer`, `branch-per-preview`; branches share
  a per-org allowance (100k writes/month + 1 GB) on every plan; creation
  takes under 5 seconds.
- **Backups and PITR** — continuous backup, restore-to-new-database, and
  branching from any point in the retention window.
- **Regions and placement** — select regions per database; placement is the
  residency guarantee (`eu-only`, `br-only`, or an explicit list) and can
  never be widened. Replicated writes cost one write per region.
- **Tenants** — a database per customer: `ts.tenants.create(key)`,
  tenant-scoped keys, per-tenant usage and branching.
- **Webhooks** — change streams with retries and signing; delivery is free.

## Tooling

- **`terrascale dev`** — a personal cloud dev branch with `--seed`, log tail,
  codegen watch, and `--faults`.
- **`terrascale push` / `diff`** — schema evolution with resumable backfills
  and field renames as first-class migrations.
- **`terrascale doctor`** — self-service diagnosis.
- **MCP** — full read-write, AI-friendly, scoped per call, audited.
