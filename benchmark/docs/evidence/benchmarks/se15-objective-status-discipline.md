# SE-15 standing objective-status discipline

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

Status: the standing practice mandated by charter PD-2026-08-26-03 D3,
delivered by SE-15 (TerraScale/TerraBaseV2#23). This document is the rule;
`scripts/check_benchmark_receipts.py` is its enforcement; the receipts under
`docs/evidence/benchmarks/` are what it governs. The status line at the top of
this document is itself subject to the rule — this record states no
measurement, so its own objective status is `inconclusive`.

## The rule

Every performance-relevant run records, on the receipt it cites:

1. **The canonical objective-status line**, exactly once, in exactly one
   shape:

   ```
   PostgreSQL-parity-or-better: met
   PostgreSQL-parity-or-better: not met
   PostgreSQL-parity-or-better: inconclusive
   ```

   One line, no markdown emphasis, no trailing prose. `inconclusive` is a
   valid and honest value, not a failure to fill in a blank.

2. **The comparison verdict** in the charter's closed vocabulary —
   `v2-faster`, `no-improvement`, `v2-regression`, `inconclusive` — on a
   line beginning `verdict:`.

3. **Both reference baselines' honest run statuses.** A reference that did
   not execute is recorded as unrun with the concrete reason and a named
   owner, never as a number and never as a silent skip.

4. **A named regression owner**, whenever the status is `not met`.

## The regression-tracking rule

> A run whose objective status is `not met` is recorded and tracked to a
> named owner, never hidden and never re-benchmarked into agreement.

- **Owner of a regression follow-up:** TerraScale benchmark lane owner
  (SE-15 follow-up), who assigns the investigation to the wave that owns
  the regressing path.
- **Owner of a missing-reference gap:** TerraScale benchmark lane owner
  (SE-15).
- The rule is a function, not a promise in prose:
  `RunRecord::regression_follow_up` returns the follow-up for `not met` and
  `None` for `met` and `inconclusive`, and the receipt renderer emits the
  owner line only when a regression exists. It is pinned by the test
  `a_regression_assigns_a_named_owner`.
- `inconclusive` owes no regression follow-up because nothing regressed;
  what it owes is the reference gap it names on its own receipt.

## Enforcement: the fail-closed checker

`scripts/check_benchmark_receipts.py` (tests:
`scripts/test_benchmark_receipt_discipline.py`) fails closed on a receipt
that is not auditable:

- no canonical objective-status line → fail;
- more than one → fail;
- a status outside `met` / `not met` / `inconclusive` → fail;
- a `verdict:` line outside the charter vocabulary → fail;
- `not met` without a `regression follow-up owner:` line → fail;
- a receipt that is not valid UTF-8 → fail, as a problem line in the
  checker's own format rather than a decoder traceback;
- a receipts directory with no receipts at all → fail, because that is the
  easiest way to make the discipline vacuously true.

Run it from the repository root:

```
python3 scripts/check_benchmark_receipts.py
python3 scripts/test_benchmark_receipt_discipline.py
```

**Scope note, stated rather than worked around:** `scripts/check.sh` does
not invoke either script. Wiring them in is a one-line edit to
`scripts/check.sh`, which is outside SE-15's declared write scope
(benchmark adapter paths, `docs/evidence/benchmarks/`, `docs/benchmarks/`), so
this packet adds the checker and its tests and leaves the wiring to the
integrator. Until it is wired, the checker is run explicitly by anyone
producing a performance receipt, and the adapter's renderer makes the
omitted line hard to produce in the first place.

## How the vocabulary is kept closed at the source

Two independent encodings of the same closed sets, so neither can drift:

- `src/benchmarks/vocabulary.rs` — `Verdict`, `ObjectiveStatus`,
  `VERDICT_VOCABULARY`, `OBJECTIVE_STATUS_VOCABULARY`, and
  `objective_status_line`, which renders the canonical line a receipt must
  carry. Tests pin every token and the exact shape of the line.
- `scripts/check_benchmark_receipts.py` — the same two sets, checked
  against whatever is actually written in `docs/evidence/benchmarks/`.

## Retroactive application to the existing corpus

The four SE-07 receipts stated their objective status in prose ("Objective
status against PD-2026-08-26-03 ... **inconclusive**") rather than in the
canonical line. The discipline's own checker failed closed on them — the
RED observation for this deliverable — and they now carry a clearly marked
addendum section with the canonical line. No measured value, verdict, or
note in those receipts was altered; the addendum only adds the
machine-checkable form of a status they already stated.

## Worked examples

A commissioning run with no comparand:

```
verdict: inconclusive
PostgreSQL-parity-or-better: inconclusive
postgresql reference: unrun — <reason> (owner: TerraScale benchmark lane owner (SE-15))
terrabase v1 reference: unrun — <reason> (owner: TerraScale benchmark lane owner (SE-15))
```

A future recorded run that regressed:

```
verdict: v2-regression
PostgreSQL-parity-or-better: not met
postgresql reference: run (PostgreSQL 18.x, observed <identity>)
terrabase v1 reference: run (fa99b9257776dbe625e6a2388c74c9ce916637ee)
regression follow-up owner: TerraScale benchmark lane owner (SE-15 follow-up)
regression tracking rule: a run whose objective status is `not met` is recorded
  and tracked to a named owner, never hidden and never re-benchmarked into agreement
```
