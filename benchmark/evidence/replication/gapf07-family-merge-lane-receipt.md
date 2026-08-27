# GAP-F07 family-merge lane receipts (R1 fold)

No production claim: lane-scoped facts from the deterministic in-process
qualification lane only. Decision record:
`docs/families/counter-merge-encoding.md` (proposed for parent
ratification).

- Commit: `c594b4b` (branch `fleet/gapf07-2`)
- Producing tests: `src/replication/lane_tests.rs`
  (`lane_family_*`), `src/families/tests.rs` (`counter_*`)
- Run commands:
  `cargo test --offline --locked --lib lane_family`,
  `cargo test --offline --locked --lib counter_`,
  `cargo test --offline --locked --lib families::`

## Scenario 1 — convergence (concurrent increments merge)

`m-a1` (owner, epoch 1) and `m-g1` (promoted at epoch 2 under a Region
partition) each commit a counter batch on the same document's `views`
counter through their member family-write paths. Healing and two-way
catch-up repair follow. Facts asserted:

- Both writers and a third member receiving both journals observe
  `views == 8` (5 + 3) and `clicks == 2`: the merged total, not a
  whole-record LWW winner.
- No increment is conflict-skipped: counter records do not compete under
  D-3 (decision D1).

## Scenario 2 — identity-scoped delta dedup (replay/duplicate delivery)

The delivery link is armed with the duplicate control while two counter
operations replicate; afterwards the origin receives the same operations
back from the follower's journal, and a promoted follower writes a later
operation. Facts asserted:

- The apply reports duplicates resolved (>= 2) and the total stays 8:
  each operation merges exactly once under duplicate frames.
- The round trip to the origin resolves by identity with the total
  unchanged (no re-merge, no ledger rejection).
- The later operation merges on top: 12 on both members.

## Scenario 3 — redelivery after a divergent merge (ledger interaction)

Two receivers applied one operation onto different local records (one had
another writer's bucket merged in, one was empty), so their ledgers bind
the same operation identity to different merged write sets. The second
receiver's journal is then delivered to the first. Facts asserted:

- The redelivery resolves by identity and keeps the merged total (8); the
  apply never surfaces the typed identity-reuse rejection (decision D3).
- This is the exact interaction the ticket names: replay idempotency
  survives a merged write set.

## Scenario 4 — delete wins over the concurrent increment

After both sides hold `views == 7`, one side deletes while the other
increments concurrently under a partition. Facts asserted: both members
observe the counter absent after repair — the tombstone dominates the
concurrent increment (decision D2), and the state-level tests prove the
tombstone survives re-merge and keeps the record alive.

## Scenario 5 — rejoin with family totals

A follower that applied a merged counter record restarts from its own
journal, rejoins with its derived cursor, and catches up. Facts asserted:
the merged total (6) survives the restart unchanged, and post-rejoin
replication merges a further operation on top (11).

## Scenario 6 — time-series exclusion pin (decision D4)

The same partition scenario drives two concurrent appends to one series.
Facts asserted: the series record converges by whole-record D-3
last-writer-wins (one entry set on both members, the loser retained as
conflict history). This pins the explicit exclusion as decided behavior,
not a silent gap.

## State-level receipts

- The merge is commutative, associative, and idempotent
  (`counter_merge_is_commutative_associative_and_idempotent`).
- Per-writer greater-sequence wins; a forged same-writer same-sequence
  entry fails closed with `RecordInvalid`
  (`counter_merge_keeps_each_writers_latest_entry_and_fails_closed_on_forged_seq`).
- A merged total outside the signed 64-bit range fails closed with
  `TotalOverflow` (`counter_merge_fails_closed_when_the_merged_total_overflows`).
- The legacy W3-F whole-value record decodes, reads, and merges under the
  reserved legacy writer tag
  (`legacy_counter_record_decodes_reads_and_merges`).
- Local delete tombstones the name; re-creation fails closed with the
  typed `TombstoneConflict` and stages nothing
  (`counter_delete_tombstones_the_name_and_fails_closed_recreation`, plus
  the updated `counter_crud...` and `counter_batch...` assertions).

## Measurements

Not measured: RPO/RTO/convergence intervals and lag — this packet's
scenarios prove merged family-record convergence, dedup, delete
precedence, and rejoin facts, not timing. No bandwidth or performance
claim is made (benchmark-lane validation for the record-size effects is
recorded as a residual, not run here).

## Limits

One deterministic run of the synthetic three-Region lane at the named
commit. The receipts prove the merge semantics at the engine and lane
seams; they make no claim about production topologies, cross-Region
timing, or tombstone-cleanup lifecycles (PB-3012 residual with the SH
wave). The writer tag is per engine instance: a restart freezes the old
bucket and starts a new one (recorded decision limit).
