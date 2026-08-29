# P0 parity status sweep

Status: recorded 2026-08-28 by the P0 truthfulness pass at base
`911197e` (clean `develop` = `origin/develop`). Machine-readable
per-row record: `docs/parity/p0-status-sweep.json`.

## What changed

All 258 parity overlays were `unresolved` with zero Proof IDs before
this pass — the charter's largest truth-in-reporting gap. This sweep
flipped **186 rows to `in-progress`** and left **72 `unresolved`**, each
backed by named file-and-test evidence or a named gap.

## Method

Six parallel read-only assessor agents (one per contract fragment:
adapters 34, documents 29, families 46, query 63,
replication-storage 49, transfer 37) graded every row against the tree:

- `in-progress` — real behavior for the row's capability exists under
  `src/` AND at least one focused test exercises it (paths + test names
  recorded per row in the JSON).
- `unresolved` — no behavior, declarations/stubs only, or no concrete
  evidence found. Conservative by rule.
- No row was marked `implemented` or `qualified`: no end-to-end,
  multi-region, recovery-lane, or benchmark receipt exists for any row,
  and the schema requires Proof IDs for those statuses. `in-progress`
  with an empty proof list is not a claim.

## Honest caveats

- The evidence strings are agent-collected pointers for the P6
  disposition pass, not Proof IDs; P6 owns converting them into
  reviewed proofs (or downgrading rows the pointers do not survive
  review).
- The replication-storage rows graded `in-progress` describe the
  current single-leader implementation; ADR 0018 supersedes that
  architecture, so those rows carry a named gap for the leaderless
  rebuild (P2) and may regress to `unresolved` if the rebuild replaces
  rather than preserves the cited behavior.
- Wire-compat rows (V1 HTTP/TCP clients, PostgreSQL wire, embedded
  server) and ETL/queue-sink rows are expected `accepted-exclusion`
  candidates under charter PD-2026-08-26-10 but stay `unresolved`
  here: an exclusion needs a reviewed disposition, which is P6 work.

## Domain summaries (from the assessors)

- **documents (29)**: sessions, transactions, metadata contracts,
  identity allocation, and change tracking are behaviorally present
  with focused tests; gaps concentrate on async forms, V1 conflict-data
  payloads, ULID identities, and `@metadata` serialization parity.
- **query (63)**: compile/execute seam, TQL/RQL parity mapping,
  statistics, includes, spatial/analyzer/vector foundations, and index
  lifecycle are present; gaps are qualification-level (cross-read
  parity vs V1, evaluator edge cases).
- **families (46)**: typed family seam with attachments, counters
  (PN-counter merge), time-series, revisions, compare-exchange, HiLo,
  lifecycle, and schema rules tested; gaps are V1 observability
  surfaces and stream sharing.
- **replication-storage (49)**: the W4 leader-per-range plane and W3-1
  storage are tested but architecturally superseded (ADR 0018/0016);
  tier manifest, mover, backup mode, and `BackupAck` do not exist.
- **transfer (37)**: streaming, backup/restore, smuggler, and logical
  migration are the most-built domain; gaps are qualification lanes
  and encryption-at-rest integration.
- **adapters (34)**: typed protocol adapter with closed operation
  registry and tenant scoping is present; the TBEP engine wiring for
  document/query/family ops refuses (`operation_refused`) and QUIC/TLS
  is absent — both named gaps.

## Regeneration

```
python3 scripts/validate_parity.py --generate
python3 scripts/validate_parity.py --check
```

Both pass: 258 items, schema 1.0, source `fa99b925…`.
