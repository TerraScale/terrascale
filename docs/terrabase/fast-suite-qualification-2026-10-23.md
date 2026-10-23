# Fast fixture qualification, 2026-10-23

PR #460 addresses #448, #447 and #414 and qualifies the existing #310 budget correction.
It changes no storage format, wire/API contract, credentials, workflow or production readiness behavior.

## Fixture roots

- #448: the shared expiry flag could be consumed by an unrelated incarnation read, and a concurrent repair could refresh the proof. Both catalog gates now finish verification before the explicit proof. Expiry targets the assertion's unique cancellation token and advances a full freshness window at the end of that incarnation read. The test asserts fresh, expired, injection consumed, then fresh after repair.
- #447: a global idle-count snapshot races background rents. The old transaction return path also uses `TryReturn`, which refuses pooling at capacity rather than guaranteeing eviction. Test-owned stores now enter the running engine's real pool through `Return`; the first eight stores cannot have idle sessions after forty returns into a pool capped at thirty-two. Shutdown must close every owned connection, leave zero outstanding connections and held database files, and release directory ownership.
- Integrated CI exposed another #447 fixture defect: directly disposing the engine while catalog convergence still runs can fault the host with `ObjectDisposedException`. The corrected fixture requests normal application shutdown and awaits `ApplicationRunCompletion` before inspecting closure and ownership. No worker failure is hidden.
- #414: the fake health check's `finally` increments `Completed` before the enclosing unwrapped `Task.Run` task finishes. The fixture now awaits the exact task used by the no-stacking guard before advancing to the next tick. The internal synchronized task accessor changes no production scheduling. The two-second deadline, `Warning,Debug` sequence, and no-stack assertions remain.

## Final focused qualification

Source: `bacb518b0a6f7fbc81d19984aa85b75a1d284ebc`, rebased onto main `b578d5b0f`.
Host: delta, .NET `11.0.100-rc.1.26425.128`, CI-pinned ts-dotnet
`bb7a398f246251683595959c7c22074f5fb5f232` in a separate checkout.
Managed through `ts-heavy-any`, nice 19; each repeat inherited verified CPU affinity `0-3`.
No test was serialized, skipped or given reduced suite parallelism.

| Test | Repeats | TUnit runner durations (seconds) |
| --- | --- | --- |
| Catalog proof expiry (#448) | 5/5 | 4.444, 5.168, 6.956, 6.727, 2.226 |
| Evicted-pool shutdown (#447) | 5/5 | 8.110, 5.852, 6.355, 3.111, 5.416 |
| Timed-out readiness (#414) | 5/5 | 0.730, 0.761, 0.930, 0.665, 0.757 |
| Throwing-logger readiness (#225, already fixed on main) | 5/5 | 0.626, 0.694, 0.764, 1.689, 0.747 |

All twenty cases passed, zero failed/skipped. Release builds passed; the vendored driver integration suite also passed.
Raw logs: delta `/home/mariogk/fast-flakes-qualification-20261023-bacb518b-ci-sdk/`.
Its `log-hashes.txt` SHA256 is
`e19bdfef54fad1a6767c2299cf2c960d6c49f387b3b8440b889f2b73694c7a70`.

Reproduce each focused case five times after building, substituting its project, class and method:

```sh
nice -n 19 taskset -c 0-3 dotnet run --project tests/TerraBase.Fast --configuration Release --no-build -- --treenode-filter '/*/*/CatalogFreshnessTests/A_proof_that_expires_during_its_incarnation_read_is_not_fresh' --minimum-expected-tests 1 --no-ansi
```

## Import contention (#310)

Merged #345 (`988ebdee53206fcab151300782a942dba19b6ac8`) corrected the test budget,
not production throughput. Baseline sixteen-import/four-CPU runs exceeded 120 seconds
in sixteen of sixteen instances. Earlier one-CPU diagnostics completed in
481.344–484.213 seconds; the longest additional 500-row batch took 55.692 seconds.
The existing budget is `ceil(484.213 + 55.692) = 540` seconds. All 3,000 rows,
partial checkpoint assertions, resumed validation and normal parallelism remain.
This PR does not raise that deadline.

Fresh stress control: sixteen independent imports concurrently sharing CPUs `0-3`,
source `21f825482a846e3a2d9c60e45a1ea3e62b41caf7`, mariogk-dev, same .NET and pinned SDK.
All sixteen passed, zero failed/skipped. TUnit runner durations were
491.010–521.709 seconds; CLI durations were 517.896–558.430 seconds including launch overhead.
This deliberately repeated-import workload leaves only 3.4% margin at its slowest
and cannot alone establish adequate CI headroom.

Raw logs: mariogk-dev `/home/mariogk/fast-flakes-qualification-20261023-21f82548-ci-sdk/`.
`import-results.json` SHA256:
`bf3fd861a479aa10613adeb196288b5f7ed75aa67d6ae8401949c6c22ad71cf3`.
`log-hashes.txt` SHA256:
`27ee5b75c6d2e7f7aa1e5bbbf4b4869060f6d2c201ef0dfee95b3ac5a6cd49de`.

## Full CI workload and headroom

The actual workload runs one cancellation/resumption case alongside fifteen other
Fast tests, rather than sixteen copies of that import. The stress control multiplies
the same 3,000-row import, cancellation, checkpoint and resume workload sixteen times.
With unchanged Makefile concurrency, the complete Fast suite on four CPUs finished
that import in **148.288 seconds**: `(540 - 148.288) / 540 = 72.5%` deadline headroom,
well above the required 30%. This supports the existing budget correction; it does
not establish a production throughput improvement or guarantee every runner's speed.

Source `bacb518b0`, mariogk-dev, same pinned SDK; managed at nice 19.
Command: `taskset -c 0-3 make terrabase-test-fast CONFIGURATION=Release TEST_ARGS='--output detailed --no-ansi'`.
The 46-CPU host had load averages `65.02 86.80 88.84` at admission.
The complete local run **failed**: 2,453 passed, nine failed, one existing #437 skip,
2,463 total, 19m44.207s. These failures were seen only on an overloaded host, not in CI:

| Test | Local result |
| --- | --- |
| `Eight_prefetched_platform_keys_allow_an_overlapping_restore_to_wait_or_cancel_without_losing_capacity(True)` | 120s timeout |
| `A_10k_line_mongoexport_file_imports_every_line_under_generated_ulids_with_oids_kept_as_fields` | 120s timeout |
| `An_exhausted_engine_read_budget_is_too_expensive` | QueryCapacityException during catalog convergence |
| `Long_document_ids_do_not_make_captured_batches_unreadable_by_the_receiver` | 120s timeout |
| `A_drop_under_a_tight_foreground_budget_releases_each_document_before_the_next(False)` | Replica did not observe the final document |
| `A_drop_under_a_tight_foreground_budget_releases_each_document_before_the_next(True)` | 120s timeout |
| `Probe_r3_cap_eviction_refills_without_skipping_local_groups` | 120s timeout |
| `A_refill_stream_deadline_retries_only_uncovered_buckets_and_certifies_every_document` | Cancellation in fixture fault setup |
| `A_filtered_scan_stops_after_10000_examined_ids_and_still_advances` | 120s timeout |

Raw log: mariogk-dev `/home/mariogk/fast-flakes-ci-case-20261023-bacb518b/full-fast.log`.
SHA256: `836f2593dcf3289473d0d3c644b175ac8e970be6d2b261a16910f444e97b7022`.
The source hashes and host load are retained beside it in `source-hashes.txt` and `environment.txt`.

On the same source head, GitHub-hosted [required TerraBase CI](https://github.com/TerraScale/terrascale/actions/runs/39950106352/job/120405221106)
passed all 2,462 executed Fast cases, zero failures and the existing #437 skip,
in 7m27.498s; native driver integration passed 22/22.
[Auth CI](https://github.com/TerraScale/terrascale/actions/runs/39950106352/job/120405221111)
also passed. Slow remains independent and non-blocking. Final merge still requires
green required CI on the exact final head.

## Scope and earlier evidence

#225 and #217 were already fixed by merged #345 and were closed with its implementation,
green required CI and recorded qualification. #437 remains owned by #423; this PR
does not edit its test or quarantine. PRs #453 and #455 affect different files.
Historical evidence is retained in `fast-suite-qualification-2026-10-22.md`.

One local attempt stopped at a compile error in the auth harness service lookup,
which was corrected. Another built cleanly but failed before readiness execution
because the host's shared SDK had moved beyond CI's pin and rejected the fixture's
internal HTTP origin. Qualification uses the exact CI SDK in a separate checkout.
Neither failure is counted as a passing test.
