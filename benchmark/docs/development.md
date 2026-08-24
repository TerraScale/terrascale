# Development contract

The workspace charter is the only source of truth for the V2 goal and
acceptance criteria. Keep this repository's documents short enough to use:

- `CONTEXT.md` defines project language and avoided synonyms.
- `docs/adr/` records accepted decisions that are hard to reverse.
- `parity/parity.schema.json` defines the machine-readable inventory shape.
- `parity/contracts/*.json` contains one domain-owned fragment of V1 contract
  facts. Each fragment declares its exact `WS-CONTRACT/<domain>` owner.
  `parity/contracts/manifest.json` pins the V1 source identity.
- `parity/overlays/PB-NNNN.json` contains the disjoint mutable status,
  disposition, and Proof IDs for one item. WS-CONTRACT owns fragments;
  WS-PARITY owns overlays and later receipts.
- `parity/inventory.json` is generated output. Workers do not edit it. Empty
  `proof_ids` and `unresolved` status are honest foundation state.
- `README.md` and `.forgejo/workflows/ci.yml` define the repeatable checks.

Run `python3 scripts/validate_parity.py --check` to validate every fragment,
overlay, source pin, status transition, and generated-output byte. Use
`--generate` only when intentionally regenerating `parity/inventory.json`.
`python3 scripts/test_parity_tooling.py` exercises the same public CLI for
duplicate IDs, malformed fragments, drift, unknown overlays, and invalid
state transitions. Draft 2020-12 direct validation is optional. The command
reports it as unrun when `jsonschema` is unavailable and never requires a
global install.

The V1 citation audit requires a real Git worktree at the manifest commit.
Run the W2 contract audit with:

```bash
python3 scripts/validate_v1_citations.py \
  --mode contract \
  --v1-root /path/to/TerraBase \
  --workspace-root /path/to/TerraScale \
  --gap-report parity/v1-test-gaps.json
```

Contract mode may finish with explicit unresolved test obligations. It must
print `CONTRACT AUDIT COMPLETE WITH N UNRESOLVED TEST GAPS`; it never treats
those gaps as passed. Release and qualification checks use `--strict`, which
fails closed for every unresolved gap. `scripts/check.sh` reads
`TERRABASE_V1_ROOT` or resolves a safe TerraBase sibling and invokes that
strict gate. If no pinned checkout is available, the audit reports `UNRUN`
and the check fails.

Feature work starts with an inventory row and a public seam. A test must cross
the same seam that a caller uses. A row changes status only when the required
V1 evidence, V2 behavior, and Proof IDs exist. Optional, legacy, and admin
rows stay visible until an evidence-backed disposition is accepted.

Storage, WAL, recovery, backup, migration, and replication work reserves the
exclusive recovery lane and produces a redacted Recovery or Replication
Receipt. Benchmark work reserves the exclusive benchmark lane and produces a
Benchmark Receipt tied to a reviewed source identity. A local release build
does not establish durability, parity, or speed.
