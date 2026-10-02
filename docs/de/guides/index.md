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
  their organization’s normal plan allowances and billing cycle, with no
  separate branch allowance. See [pricing](/de/pricing/).
- **Backups and PITR** — intended continuous backup, restore-to-new-database,
  and branching from the retention window; the producer workflow is awaiting
  qualification.
- **Regions and placement** — select regions per database; placement is the
  residency guarantee (`eu-only`, `br-only`, or an explicit list) and can
  never be widened. Writes consume operation units once per selected Region
  before consuming the organization’s shared allowance.
- **Tenants** — the intended database-per-customer model, with
  tenant-scoped keys, per-tenant usage and
  branching. Tenant provisioning is awaiting its producer contract.
- **Webhooks** — change streams with retries and signing; delivery is free.

## Tooling

- **`terrascale dev`** — a personal cloud dev branch. The CLI supports data-file
  seeds, bounded branch diagnostic polling, and local schema codegen watch.
  TypeScript seed functions are not executed. See the installed CLI’s help
  for its available flags; cloud integration still requires qualification.
- **`terrascale push` / `diff`** — intended schema evolution with resumable
  backfills and field renames; the full workflow is awaiting qualification.
- **`terrascale doctor`** — self-service diagnosis.
- **MCP** — intended scoped, audited access; the public read-write workflow is
  awaiting qualification.
