# Development contract

The workspace charter is the only source of truth for the V2 goal and
acceptance criteria. Keep this repository's documents short enough to use:

- `CONTEXT.md` defines project language and avoided synonyms.
- `docs/adr/` records accepted decisions that are hard to reverse.
- `parity/parity.schema.json` defines the machine-readable inventory shape.
- `parity/inventory.json` records V1 obligations. Empty `proof_ids` and
  `unresolved` status are honest foundation state.
- `README.md` and `.forgejo/workflows/ci.yml` define the repeatable checks.

Feature work starts with an inventory row and a public seam. A test must cross
the same seam that a caller uses. A row changes status only when the required
V1 evidence, V2 behavior, and Proof IDs exist. Optional, legacy, and admin
rows stay visible until an evidence-backed disposition is accepted.

Storage, WAL, recovery, backup, migration, and replication work reserves the
exclusive recovery lane and produces a redacted Recovery or Replication
Receipt. Benchmark work reserves the exclusive benchmark lane and produces a
Benchmark Receipt tied to a reviewed source identity. A local release build
does not establish durability, parity, or speed.
