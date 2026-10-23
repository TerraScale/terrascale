# Store and discovery failure evidence

Issues: #400, #333, #334, #329, #335 and #299.
Fix: [PR #463](https://github.com/TerraScale/terrascale/pull/463).

## Root causes

- #335 and #299: local ACK bookkeeping can encounter a busy writer after the
  receiver has durably acknowledged a batch. Resetting to the older persisted
  cursor on the same stream replays confirmed rows. Preserve the confirmed
  cursor until reconnect, scoped to database incarnation; keep receiver checks.
  The [original #299 job](https://github.com/TerraScale/terrascale/actions/runs/38706849238/job/116328296654)
  records busy peer bookkeeping at 2026-10-15 08:43:20 UTC and the same trace's
  growing-change-ID rejection at 08:43:32, before the reported HTTP timeout.
  A busy bookkeeping failure also imposed a 30-second shipping delay, including
  when a newer batch prevented reset. Retry contention after one second and
  preserve newer in-flight work. The regression now requires progress within
  the existing ten-second confirmation bound and checks the minimum source
  read position directly; the prior code fails the tighter bound.
- #400: periodic catalog writes can share the cache's invalidation stripe and
  correctly prevent promotion during a read. Freeze the capacity test's clock
  to preserve its no-intervening-writes premise. All exact eviction assertions
  remain intact.
- #333, #334 and #329: the auth test relay closed excess pooled connections at
  its old 64-connection limit. Existing main commit
  [cd16a8a0d](https://github.com/TerraScale/terrascale/commit/cd16a8a0d0eddfff7cf9266768421521065322f4)
  raises the limit to 1024 and adds the 128-connection Fast regression. These
  reports precede that fix; no auth implementation change is needed here.

## Remaining large-drop cause

PR #463 closes #400, #333, #334 and #329. It fixes the growing-change-ID cause
in #335/#299, but those two reports remain open for a separate progress-frame
PR. #277 follows afterward in its own PR.

The raw [Slow log on f16831749](https://github.com/TerraScale/terrascale/actions/runs/39983399302/job/120519678313)
still fails the post-drop PUT. A traced local reproduction passed but showed
eight no-response ACK timeouts: one collection-clear delta expands into 16,896
derived document writes in one durable transaction. The fixed 15-second batch
deadline can cancel that transaction while it is still making progress.
A GET returning 404 proves the drop committed, not that its ACK arrived.
No coverage-refused ACK or refill occurred in that trace; this differs from
#475's coverage-refill backoff. No receiver checks or deadlines were weakened.

## Five-repeat proof

Tested source: `90f917e0f2fbdf6875a8ad8a32c72ddd05609f3e`, before the
rebases onto later main commits. Host: `delta`. Configuration: Release.
.NET SDK: `11.0.100-rc.1.26425.128`.

| Test | Passed | Failed/skipped |
|---|---:|---:|
| `A_busy_ACK_writer_does_not_replay_confirmed_groups_on_the_same_stream(False)` | 5 | 0 |
| `A_busy_ACK_writer_does_not_replay_confirmed_groups_on_the_same_stream(True)` | 5 | 0 |
| `Many_small_query_pages_evict_by_entry_count_before_the_byte_budget` | 5 | 0 |
| `A_drop_of_more_documents_than_one_owner_may_lease_commits_locally_and_replicated` | 5 | 0 |
| `Concurrent_independent_stores_have_exactly_one_unique_claim_winner` | 5 | 0 |
| `Stale_deletion_cannot_remove_a_replacement_at_the_same_scoped_key` | 5 | 0 |
| `Discovery_reads_return_to_their_own_region_once_it_answers_again` | 5 | 0 |

Each named method ran in a separate process per round, using TUnit's default
parallel execution. The busy-ACK method includes both argument cases. The
stable-only case verifies neutrality; the ACK case exercises the replay bug.
The unchanged large-drop test also verifies a later write and both directions'
acknowledgements after collection recreation.

Build with `nice -n 19 ts-heavy make terrabase-build CONFIGURATION=Release`
and `nice -n 19 ts-heavy make auth-build CONFIGURATION=Release`, setting
`TsDotnetRoot` to the SDK checkout. The auth runner's `TERRABASE_BINARY` names
the TerraBase executable built from that source. For each method, repeat this
five times with its project and name:

```sh
nice -n 19 ts-heavy dotnet run --project <project> --configuration Release --no-build -- \
  --treenode-filter '/*/*/*/<method>' --minimum-expected-tests 1 \
  --output detailed --no-ansi --report-trx --results-directory <unique-directory>
```

Retained artifacts: `/tmp/store-discovery-proof` on delta. All 155 manifest
entries verified; 30 TRX files contain exactly 35 Passed results, zero failures
and zero skips. Manifest SHA-256:
`1af5a5ece64236c8bec0174dbb775116d1f6c7876fb9390c012231594b12d4e7`.
Runner script SHA-256:
`6367dd3ac6155c6f5c2438497d6fa941e49204aac6a5b6a16ed9681efaf09bb1`.

## CI limits

[Run 39947556827](https://github.com/TerraScale/terrascale/actions/runs/39947556827)
passed required checks on the tested head. TerraBase Fast recorded 2464 passes,
zero failures and the existing #437 skip; the driver recorded 22 passes.
Non-blocking Slow recorded 63 passes, 15 failures and one existing host-fixture
skip. Six SQL fixture assertions overlap PR #456; restart/WAL-shim failures
overlap PR #457 (the image lacks `cc`). Neither targeted Slow test failed.
The rebased merge head must independently pass required CI.
