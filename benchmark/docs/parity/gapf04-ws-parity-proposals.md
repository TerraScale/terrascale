# WS-PARITY proposals from GAP-F04 (ticket #73)

Proposals only — the parity overlay and its generated inventory are
integrator-owned; this builder mutates none of them (charter shared-file
rules). Each proposal cites the decision record and the producing tests.

## PB-3019 time-series-rollup-retention-config

Proposed disposition: `seam-implemented` for the executed scope — rollup
generation and retention cleanup now run as engine-side step operations,
not just stored configuration.

- Decision record: `docs/families/gapf04-timeseries-execution.md` (D1
  execution owner, D2 incremental encoding, D3 retention and the GAP-R02
  watermark interaction).
- Implementation: `src/families/timeseries/` (`rollup_series`,
  `enforce_retention`, `run_series`); derived rollup sections are encoded
  inside the series record (`src/families/state.rs`).
- Evidence: `src/families/timeseries/tests.rs` — window alignment and the
  completeness boundary, per-aggregation arithmetic, idempotent re-runs,
  horizon deletion with the pending-rollup guard, and the tombstone
  evidence-window skip. Receipts in the decision record's Evidence section.
- Remaining deltas (keep the row honest): the maintenance tick has no
  production driver yet (host supervision or an engine maintenance seam owns
  the schedule and the cross-process single-writer election V1 gets from
  first-in-topology); rollup policies aggregate from the raw series, not
  chained policy-to-policy as V1 does (accepted difference, decision D2).

## PB-3024 time-series-errors

Proposed disposition: unchanged owner set, with the case set extended by
this packet: a rollup selector naming a rollup absent from the stored
policy is the existing typed `TimeSeriesPolicyInvalid`
(`src/families/store.rs`), so a removed rollup stops being addressable
instead of serving stale points. Producing test:
`paging_applies_to_rollup_reads_and_unknown_rollups_refuse`.

## Streaming page rows (time-series bounded reads)

Proposed disposition: the family-seam half is now implemented —
`TimeSeriesPaging` bounds a page with a cursor-stable exclusive timestamp
cursor and reports the matched total and the next cursor
(`src/families/payloads.rs`, `src/families/store.rs`). The streaming
source already slices its own batches over the whole page and is
deliberately unchanged; wiring the streaming source's selector to request
bounded pages directly is a streaming-owner follow-up, not required for
the family contract. Producing tests: `paging_chains_bounded_pages_to_exhaustion`,
`paging_cursors_are_stable_against_late_older_inserts`,
`paging_limits_are_validated_at_the_seam_bounds`.
