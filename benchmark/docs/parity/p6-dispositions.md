# P6 parity disposition pass

> Historical phase record. Any disposition depending on Loro, UDS,
> repository-local C# hosts, leader or range ownership, mandatory NATS, or the
> former auth boundary is reopened under ADR 0019.

Status: recorded 2026-08-29 by the P6 pass at develop `5c041b8`.
Machine-readable record: `parity/overlays/PB-*.json` (status,
disposition, proof IDs) and `parity/proofs.json` (the Proof ID
registry).

## What changed

Every one of the 258 parity overlays now carries a REVIEWED
disposition — the P0 sweep's evidence pointers were re-graded against
the tree as it stands after P1-P5 (LSM engine, leaderless
coordinators, TBEP serving over UDS and QUIC, tier administration,
cluster identity and backup clusters, `BackupAck`, the NATS control
contract, expansion with catch-up, backup-last routing), and each row
now states what the V2 tree does for it and why its status is honest.

- **29 rows → `implemented`**, each with a minted Proof ID
  (`P-001`..`P-029`, registry in `parity/proofs.json`): real behavior
  under the tree AND at least one focused passing test exercising the
  row's public-contract semantics, every citation verified twice (the
  workflow's adversarial verifier, then an independent identifier
  re-check across `src/`, `crates/`, and `host/`).
- **169 rows hold `in-progress`** with a named gap in the disposition:
  real behavior exists but no focused test exercises the row's
  semantics, or a qualification obligation (multi-region fault
  evidence, benchmark parity, live NATS/S3) remains that belongs to
  P7, or the cited architecture is superseded.
- **60 rows hold `unresolved`** with the disposition naming exactly
  what does not exist (V1 wire-compat surfaces, authorization
  taxonomy, CORS, content encoding, V1 method vocabulary, admin route
  families). No row was excluded silently: no accepted V2 difference
  was proposed, because the graders could not tie a single exclusion
  to a charter decision that names it — the honest outcome is that
  PD-2026-08-26-10 keeps backup, sharding, optional, legacy, and
  admin gaps IN parity scope, and the wire-compat rows carry their
  gaps as `unresolved` rather than as unreviewed exclusions.

## Method

A 12-agent workflow (six graders — one per contract fragment — each
feeding an adversarial verifier): the graders read the frozen contract
facts, the P0 sweep pointers, the charter, and the P1-P5 receipts
ledger, then graded every row against the current tree by search and
reading; the verifiers grepped every `implemented` citation and
checked every proposed exclusion against the charter, downgrading
whatever did not survive. A final independent re-check re-resolved
every surviving citation by identifier search.

## Per-domain outcome

- **adapters (34)**: 2 implemented, 19 in-progress, 13 unresolved
- **documents (29)**: 27 in-progress, 2 unresolved
- **families (46)**: 5 implemented, 36 in-progress, 5 unresolved
- **query (63)**: 2 implemented, 37 in-progress, 24 unresolved
- **replication-storage (49)**: 18 implemented, 27 in-progress, 4 unresolved
- **transfer (37)**: 2 implemented, 23 in-progress, 12 unresolved

The implemented rows: PB-1000, PB-1012 (adapters); PB-3009, PB-3015,
PB-3024, PB-3031, PB-3033 (families); PB-4005, PB-4006 (query);
PB-5000, PB-5004, PB-5005, PB-5008, PB-5013, PB-5014, PB-5016,
PB-5018, PB-5020, PB-5023, PB-5026, PB-5033, PB-5034, PB-5036,
PB-5038, PB-5042, PB-5043, PB-5045 (replication-storage); PB-6000,
PB-6010 (transfer).

## Honest caveats

- `implemented` is a behavior-and-test receipt, not a qualification:
  multi-region fault evidence, recovery-lane receipts, benchmark
  parity, and live-broker/object-store runs are P7 and are named as
  gaps in the affected rows' dispositions.
- The graders' citation formats vary (bare test names, `path::fn`,
  `fn (path)`); the registry preserves them verbatim alongside the
  verification method.
- `scripts/test_parity_tooling.py` fixtures were updated in the same
  landing: two tests mutated PB-1000 assuming it still had no proofs
  or disposition, which this pass made untrue; the mutations now
  explicitly clear the fields they test, keeping the validator rules
  under test unchanged.

## Regeneration

```
python3 scripts/validate_parity.py --generate
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
```

All pass: 258 items, schema 1.0, source `fa99b925…`, 9/9 tooling
tests. `scripts/check.sh` not run (AC-12).
