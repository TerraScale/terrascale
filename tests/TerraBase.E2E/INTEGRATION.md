# Release candidate integration manifest: integration phase 1

Owner: the strict-test owner, the single integration owner and the single publisher of acceptance candidates (#108).

**Status:** local only. Nothing is pushed, merged or deployed: the COS global merge hold and preview push freeze apply,
and the production deploy and previews stay paused.

**Status 2026-10-12:** manifest written; nothing combined yet. T1 reported done (item 4 pinned at `1c800e32`). Combining waits on R10a (its gate-blocker fix and R3's verification) and on the CI optimizer's vendor-filter PR (see "Waiting on").

## Base

`main` at `c691f947` ("ci: turn off PR preview environments (#123)"), on top of `f7efa1a7` ("ci: pause the production
deploy job (containment 1C) (#122)").

The integration keeps `.github/workflows/ci.yml` exactly as it is at `c691f947`:
- the deploy job paused (#122);
- `if: false && github.event_name == 'pull_request'` on the preview job (#123).

A conflict in those lines is resolved to main's version.

## Merge train (user order 2026-10-12 14:58Z: the merge hold is lifted; deploy pause #122 and previews off #123 stay)

Phase 1 is now merged to main in manifest order, and acceptance runs on main's head. Each row is recorded at merge time:
- the PR's exact head, with its `test` run and conclusion on that head;
- the review and finding state;
- the main SHA after the merge, with its push run;
- the push run must show `deploy` and `preview` **skipped**, with both `if: false &&` lines present in main's `ci.yml`.

**Merge method** (the integration owner's call):
- squash (repo convention) for standalone PRs;
- a merge commit for #109, #110 and the #115 vendored chain, because their commits are ancestors of T1's chain and the
  pristine upstream import must stay an identifiable commit.

| PR | Head | `test` on that head | Reviews / findings | Method | Main after | Main push run | deploy / preview | Gates in ci.yml | Main run `test` |
| --- | --- | --- | --- | --- | --- | --- | --- | ---: | --- |
| #129 | `165295e0` | 38262406479 success | none open (0 threads; 0 reviews) | squash | `986d8f75` | 38267208680 | skipped / skipped | 2 | success |
| #128 | `a71c8f4b` | 38262350977 success | none open | squash | `84f85db5` | 38267257956 | skipped / skipped | 2 | success |
| #126 | `27fa4fff` | 38255314938 success | none open | squash | `f51024c1` | 38267295524 | skipped / skipped | 2 | success |
| #109 | `c4b15068` | 38212020311 success | none open | merge | `5713364d` | 38267358851 | skipped / skipped | 2 | **failure**: Fast `A_storage_thread_busy_with_a_long_read_does_not_hold_up_writes` |
| #110 | `3cf9f3a5` | 38212037240 success | none open | merge | `daaa20f5` | 38267398256 | skipped / skipped | 2 | **failure**: Fast `Parallel_writers_cannot_claim_the_same_value_in_a_group_commit` |
| #107 | `00390b11` | 38210892351 success | none open | squash | `fd98faf0` | 38267560646 | skipped / skipped | 2 | success (Slow success) |
| #101 | `0fc542f5` | 38205238217 success | none open (docs) | squash | `5de6d0cc` | 38267600876 | skipped / skipped | 2 | success (Slow success) |
| #124 | `350f6608` | 38253803713 success | none open | squash | `20afa8cd` | 38267640230 | skipped / skipped | 2 | success (Slow success) |
| #105 | `4b85b32c` | 38253833012 success | none open | squash | `014c9928` | 38267752015 | skipped / skipped | 2 | **failure**: Fast `Parallel_writers_cannot_claim_the_same_value_in_a_group_commit` |

**MAIN IS RED (COS stop, 15:20Z):** Fast fails intermittently on main and on PR runs over current main, with a
different test each time:
- `A_storage_thread_busy_with_a_long_read_does_not_hold_up_writes` (main 38267358851);
- `Parallel_writers_cannot_claim_the_same_value_in_a_group_commit` (main 38267398256 and 38267752015);
- `Schema_failures_name_the_transaction_operation_and_roll_back_every_write_and_offset(False)` (#130, 38267360406);
- `Schemas_persist_across_restart_and_drop_recreate_removes_them` (#79, 38268608663).

T1 (owner of #109) is fixing it. **No further product-code PR merges until main is green.** T1's fix may go in.
Test-only and docs PRs may merge only with green exact-head runs, and their record notes main's state.

**Preview apps destroyed by merges.** The merges at 15:01–15:06Z ran `previews.yml` on their close events. That
destroyed these preview apps **with their volumes and buckets**, before the user's exact deletion confirmation (the
user had ordered all previews deleted). **`previews.yml` (workflow 381612805) is DISABLED since 15:24Z**
(`disabled_manually`, verified by COS; no cleanup run was queued or running), so later merges no longer delete preview
apps.

| PR | App | previews.yml run |
| --- | --- | --- |
| #109 | ts-pr-109 | 38267357958 |
| #110 | ts-pr-110 | 38267399078 |
| #107 | ts-pr-107 | 38267561035 |
| #101 | ts-pr-101 | 38267601424 |
| #105 | ts-pr-105 | 38267753104 |

#129, #128, #126 and #124 also ran `previews.yml` on close (38267208978, 38267258398, 38267296565, 38267640482); they
had no preview app. PRs that still have a preview app: #79, #88, #106, #108, #111–#121. Since the workflow is disabled, merging them no longer destroys their apps.

**Train order after main is green:**
1. **#115 first.** It carries T1's vendored chain `8dc69d14` with main merged in. COS hypothesis: main's five flaky Fast
   tests are the Turso abandoned-statement finalizer race, and main has #109 but not the driver fix, so #115 is the
   likely fix.
2. Then the other PRs, in manifest order.

- **#115 at `787ebc57`** (contains `8dc69d14` and main `014c9928`; no `.github` change): `test` **failed**, run 38272693302. libs, auth and terrabase all fail in `make fmt-check`: `dotnet format whitespace` over the solution now covers the pristine vendored driver (`FINALNEWLINE`, `CHARSET`). Handed to T1: exclude `src/vendor` from the whitespace check without editing the pristine import. Not merged.

**Every PR needs a fresh green `test` run on its exact head after main is green.** A rerun of a failed job while main is
known flaky proves nothing. #79's 15:30Z rerun request (run 38268608663) does not count.

**#132 before #133 (the integration owner's call).** #133 carries #132's change as its first commit, and PRs are never
closed, so a superseded #132 would stay open for good. #132 therefore merges first; its contract tests already pass on
main. #133 then merges main in, the duplicate commit's content is already present, and only R3's routing commit remains.
Each gets its own fresh green `test` on its exact head.

**Six distinct product Fast flakes** are seen so far, the five above plus
`Declared_JSON_schema_and_native_uniqueness_coexist_without_changing_collection_identity` on #132's run 38270496291.
All are routed to T1.

**Not merged until fixed and verified:**
- **R10a's stack**, until the no-peer fail-open and the unprotected override are fixed and R3 verifies in writing;
- **#125**, until its metrics race is fixed (#130) and a fresh `test` run on its exact head is green;
- anything whose Slow or metrics fix is still open.

**No PR is closed.** Closing destroys its preview app, which is the user's pending cleanup decision.

## Integration phase 1: items, in combine order

Commits marked "local" exist only on delta, in the shared object store, under the push freeze. The combine uses exactly
the SHA listed. A newer head means a new manifest entry, never a silent swap.

| # | Item | PR | Use (SHA) | Storage format | State / gate | Known exclusions and notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Turso plan budget; a stopped read fails only its own batched write | #109 | `c4b15068` (pushed) | 3 (unchanged) | done | Inside item 4's pin. |
| 2 | Finalize failed Turso statements on their connection's thread; no collection for a stopped read | #115 | `9da71771` (local; pushed head `51a363e1`) | 3 | done | Inside item 4's pin. |
| 3 | Native statement runner | none | `78795796` (local) | 3 | done | Inside item 4's pin, which **removes** the native runner again (`de7ed025`: the provider path, measured equal). |
| 4 | **T1 vendored Turso driver** | none (local only) | **`1c800e32`** (local `fix/turso-vendored-driver`; supersedes `af69cd57`) | **3** (`3-2a5ee125ee78`, unchanged) | **done** (T1, reported to COS) | Contains items 1, 2, 3 and 5. The pristine upstream import `b3e9812e` (tursodatabase/turso v0.8.2 `5c168be5`), 3 driver patches (`39f687aa`, `17e02b3d`, `f4dcaa3a`) each with a fail-first test, `UPSTREAM.md` and `scripts/vendor/update-turso-driver.sh`, and the DrainBench cleanup `b6ea82c2`. `make terrabase-test-fast` now runs the 4 driver tests before Fast. Behaviour change accepted by COS: a stopped or cancelled read ends its read transaction (`225713f0`). **No `.github` change**: the path-filter commit is gone, and the COS push check passes. |
| 5 | A count examines tombstones again (#68) | #110 | `3cf9f3a5` (pushed) | 3 | done | Inside item 4's pin (merged at `b5805f13`). |
| 6 | Slow: provision the real ts-auth signing key (#102) | #107 | `00390b11` (pushed) | none (tests) | ready | Slow only, non-blocking suite. |
| 7 | R3 CDC decode notes | #101 | `0fc542f5` (pushed) | none (docs) | ready | |
| 7a | Backup: one lifetime S3 response handler dispatching per request, replacing the per-call attach/detach race that threw a NullReferenceException in the AWS SDK (`BackupKeyRotationTests`) | #128 | `a71c8f4b` (pushed, base main) | 3 (`storage-format-unchanged`) | ready | Its regression fails 3/3 on main and passes 5/5 (T1). Product fix; push check passes (no .github change). |
| 7b | Tester C: regional routing and ts-auth token contract tests (test only) | #132 | `833d21e2` | none | ready; merges after main is green, with a fresh green `test` | Tester C's `2b86f784` cherry-picked unchanged onto main `014c9928`. Local: auth Fast 593/593, terrabase Fast 1424/1425 (one product flake). First CI run 38270496291: auth success; terrabase failed on the product flake `Declared_JSON_schema_and_native_uniqueness_coexist_without_changing_collection_identity`, not a contract test. |
| 7c | R3: `<region>.api.terrascale.tech` pins a region and fails closed | #133 | `79ebd2c2` | 3 | ready; merges after #132 and after main is green | Contains Tester C's commit (`97ed1d7c`, the same content as #132) plus R3's `79ebd2c2`. That commit changes `Listeners.cs`, the docs (issuer doc fixed) and +197 lines of routing contract tests. Behaviour: a regional host is answered only in its own region; an unavailable region answers 503 `region_unavailable` (never served locally, including replays to the wrong region and `fallback=prefer_self` failures); an unknown label answers 421; a host/pin mismatch answers 421 with no replay. R3's results: Fast 1431/1431, auth 578/578, host-parsing mutant fails 8/8. Push check passes. |
| 8 | R2c-2: delete the per-collection change feed and history tables | #88 | `05bd4374` (pushed) | **4** | ready; the integration owner resolves it against `c691f947` during the combine, and **T1 reviews that resolution in writing before the freeze** (COS decision; #88's owner is finished, no separate rebase push) | It shares the feed area with R8 (item 13), so conflicts are expected there. Its format 4 combines with R10a's (see "Storage format"). |
| 9 | R3: CDC change group source | #106 | `4c32da5e` (pushed) | 3 | ready | Head of `2328b8db`, `2ee8b148`, `b838729e`, `81fa4fcb`, `4c32da5e`. |
| 10 | RegionProcesses (regions as OS processes) | #111 | `07f8d81f` (pushed) | none (tests) | ready | |
| 11 | Replicated index DDL; unique re-projection | #112 | `8b8758ff` (pushed) | 3 | ready | |
| 12 | SIGKILL before shipping across processes; lag series | #113 | `b383cb84` (pushed) | 3 | ready | |
| 13 | R8: database changes feed as SSE | #118 | `9277893f` (local; pushed head `90c1b833`) | 3 | ready | `9277893f` adds only the benchmark relabel over `90c1b833`. It sits on #111 and merges into the R3 line. |
| 14 | R8 e2e scenarios | #119 | `90847616` (local; pushed head `56b9ffea`) | none (tests) | ready | Only `ChangesFeedE2ETests.cs`. Its two `[Scenario]` declarations come from the assertion side (item 19). |
| 15 | R6: anti-entropy and refill | #114 | `594b052a` (pushed) | 3 | ready | |
| 16 | Register once (harness for R10a) | #117 | `256d5442` (pushed) | none (tests) | ready | |
| 17 | **R7: garbage collection**, copying refill | #120 | **`24e138c0`** (R3's local R7 tip; supersedes `76cb23ff`, which it contains) | 3 (R7 changes no format, #120) | ready; merges after its base PRs | Contains `e8b458e5` (stale writes), `7d83cfbf` (refill refused), `80c67345` (the PeerLink abort fix), `baa2e236` (register as the first region's user), `76cb23ff` (ack past CDC rows with nothing to ship) and the Fast restarted-machine wait. R10a's stack sits on it. |
| 18 | **R10a: catalog as a lattice database** | #116, #121 | **`60eae912`** (local branch `r10a-on-r7gc`, on R7 `24e138c0`; supersedes `a54beab4`/`r10a-on-r7`): `305890b3` R10a-1, `2334a941` R10a-2 (format 4), `a0b86d7d` R10a-3, `959904dc` e2e, `f03c62e7` invariants, `db573c42` bounded delete, `84dff445` fail-closed restore gate, `a9c56e8f` QuietAsync routing, `60eae912` restore-gate blockers (no opening on an empty peer list; explicit genesis bootstrap; no unauthenticated override) | **4** (`4-44d5cdc5b81c` alone, per R10a) | **WAITING: R3's written verification of `60eae912`** | R10a's results at `60eae912`: Fast 1392/1392; E2E 34 passed + 25 landed, 3 skipped, 1 failed. The failure is the single-region storage stall, whose harness now needs a catalog bootstrap; that harness change is asked of R3. #116 and #121 are retargeted to main only after their base PRs merge (#120 → #117 → #114 → … → #106). R10a then brings both to the current head and runs the `.github` check before pushing. |
| 19 | Assertion side and recorder | #105 + the candidate stack | #105 `4069da9f` (pushed) + local `94086cd6`/`4b85b32c`; the candidate-9 assertion commits on `dea6b364` + R3's `1b68b852` (writer outcomes) | none (tests) | ready | The final `Gaps.cs` follows the candidate stack: it keeps only gaps that still fail their own way. #105's main-only setup-blocked classification is kept only if a scenario still needs it. |
| 19a | **Tester A: multi-writer convergence matrix** (`tests/TerraBase.E2E/MultiWriterMatrixTests.cs`, test only) | none | **`3ade7009`** (local, on `cfda0499`); the combine takes the file only | none (test only; no product or harness change) | ready; **needs #111 (`RegionProcesses`) on main first** | 1 test × 6 write orders over region processes (SIGKILL windows, all-regions restart): same and different field, delete vs edit both orders, counters, add-wins sets, delete → re-create, counts and **exact per-region digests**. **Ack-based barrier:** `/replication` Head advanced past the pre-sentinel snapshot, and Acknowledged ≥ Head per receiver. Tester A: 5 quiet repeats 30/30 pass, mutants M1 and M2 fail 6/6. Declared `[Scenario(Nodes.Processes, Faults.ProcessKill)]` by the strict owner. **Caveat to state with its result:** no route exposes the true CDC head, so "anti-entropy out of order and a stale Head at the same moment" is not formally excluded. Hook request to R3: expose the CDC head per database. It joins the test-only PR with Tester C's files only once #111 is on main; it uses `RegionProcesses`, which main does not have. |

**Phase 2 (required, later).** These are not part of the phase-1 candidate, and phase 1 does not grow to take them:

| Item | Owner | Closes |
| --- | --- | --- |
| R9: SQL writes merge across regions | R3 | E11 (row 27) |
| R11: home region and live move | R10a | E13 (row 25) |
| R12: ts-auth refresh reuse, the auth half | Dogfood | E15's ts-auth half (row 28) |
| #79: Fly 1M benchmark driver | strict-test owner (test driver) | **Acceptance dependency:** at least 1M documents; counts and checksums matching in all 3 regions; latency, CPU and RAM; results in `benchmark/bench/` |

ts-dashboard is held by the user and is in neither phase.

## CI file

`.github/workflows/ci.yml` resolves to main's version at `c691f947`, keeping its preview and deploy pause lines.
- **Item 4 no longer touches CI.** T1 removed `8915e6d2`'s three vendor path filters from the chain (`1c800e32`).
- **Resolution in the combine (COS decision):** take main's `ci.yml` exactly.
- **Required before the phase-1 merge: #129** ("ci: run area checks for vendored Turso changes"), the CI optimizer's own
  base-main PR, at exactly `165295e00edea4ccc8374068ce4b5a75a41acae3`.
  - It adds the vendored paths to the area filters (`ci.yml` +14) plus `scripts/ci/path-filter-test.py`, with 13 matcher
    fixtures, passing locally per the CI optimizer. It makes no Makefile, Dockerfile or result-check change.
  - **Hosted checks:** run 38262406479 on `165295e0` **completed with success** (checked 2026-10-12): changes, libs,
    auth, terrabase and test passed; preview, deploy, e2e and slow were skipped.
  - The COS push check passes: the merge tree with main (`d9804dbb`) keeps both gates and adds no workflow file.
  - Until it lands, a vendor-only change could skip the terrabase tests, so phase 1 does not merge before it. It is
    separate from #125 (performance).
- **Pre-push check:** every pushed head still runs the COS check (both `if: false && …` gates kept, no new workflow
  file).

## Storage format

- **Main is format 3.** `StorageFormat.Id` is `{Version}-{first 12 hex of the schema digest}`, and a build refuses a data
  directory of another Id (no migration).
- **#88 (R2c-2) and R10a-2 each bump `Version` to 4**, for different reasons (#88 deletes the feed and history tables;
  R10a-2 makes `_system` a lattice database). Combined, they make one format: `Version` 4, plus the digest of the
  combined schema. This is the final format COS expects ("R10a's format 4").
- **Every other item keeps format 3**, labelled `storage-format-unchanged` where it is a PR.
- **No data directory of an intermediate format-4 build is reused.** A region rebuild starts empty (greenfield).
- **The exact Id is recorded from the frozen SHA's binary** (`terrabase --storage-format`, see "Acceptance"). That Id is
  the image the region rebuild uses.

## Combine procedure (once, after every gate is done)

1. Branch `test/release-candidate-<n>` from `c691f947`.
2. Merge items 1–19 in order, keeping each PR's own commits (merge commits, no squash).
3. Resolve conflicts:
   - the owner of each side reviews any product-code resolution before the SHA is frozen;
   - `ci.yml` keeps main's paused lines;
   - `Gaps.cs`, `COVERAGE.md` and `RUNS.md` are the strict owner's.
4. Build (`make terrabase-build`) and run Fast (`make terrabase-test-fast`), under `flock -o /tmp/ts-heavy.lock`.
5. Freeze the resulting SHA on that named branch, with no further commits. Publish it on #108 with:
   - its base and the exact SHA of every item;
   - the conflicts and how each was resolved;
   - its storage format Id;
   - its exclusions.

## Acceptance on the frozen SHA

- **Runs:**
  - The strict owner and the R3 owner each run 3 `make terrabase-e2e`, independently, from their own worktrees and
    ts-dotnet worktrees.
  - Each run uses `flock -o /tmp/ts-heavy.lock` with `E2E_STACKED_ON` set to the frozen base.
  - Host captures (`uptime`, `nproc`, `free -m`, current top CPU) at the start and end of each run, labelled quiet
    (load1 ≤ 8 on 16 CPUs) or loaded.
- **Results:**
  - Failed runs are kept and never overwritten.
  - Results count only after the R3 owner acknowledges the exact SHA on #108.
  - No new candidate while a batch runs; fixes go into the next named candidate.
- **Storage format:** record `dotnet src/TerraBase/bin/$(CONFIGURATION)/net11.0/terrabase.dll --storage-format` (the build the acceptance runs used), or the built
  image's `terrabase --storage-format`, at the frozen SHA. It goes in the #108 publication and in `COVERAGE.md`.
- **Release gap: Slow FAILS on main `c691f947`.** Run 38251442518 has the `slow` job failed; the workflow shows success
  only because Slow is non-blocking. The open failures:

  | Issue | Failing Slow test | Fix (phase 1 item) |
  | --- | --- | --- |
  | #102 | `Access_tokens_from_the_real_ts_auth_authenticate_on_the_management_api` (hung) | #107 (Dogfood), item 6 |
  | #103 | `Enterprise_can_execute_beyond_the_pro_sqlite_instruction_limit` | #109 (T1), item 1 |
  | #104 | `Statements_past_the_engine_work_budget_are_too_expensive_even_before_their_first_row` | #109 (T1), item 1 |
  | #68 | `Assert_count_examines_at_most_10000_rows_whatever_it_matches` | #110 (T1), item 5 |

  The release is not green until Slow passes on main. Acceptance of the frozen candidate also runs
  `make terrabase-test-slow` and records it.
- **Before acceptance counts (vendored driver, item 4):**
  - an actual driver build plus the driver tests (`tests/Turso.Data.Sqlite.Tests`, run first by
    `make terrabase-test-fast`) in the **combined tree** at the frozen SHA, recorded in the publication;
  - T1's dependency and security audit of the vendored driver (requested by COS), recorded with its result.
- **Still blocked after acceptance:**
  - the open 500 ops/s write-floor gate (`benchmark/bench/2026-10-12-write-floor-open-gate.md`, #79 branch, local);
  - the Fly runs (credential handoff, holds).

## Waiting on

1. **T1:** done (`1c800e32`).
1a. **CI optimizer:** #129 at `165295e0`, hosted run 38262406479 green. Required before the phase-1 merge, not before the combine.
1b. **T1:** the dependency and security audit of the vendored driver. Required before acceptance counts.
2. **R10a:** R3 found one more blocker in `60eae912`: a single remaining machine never reopens the catalog. R10a is implementing an operator catalog-open request file. The stack waits on that fix and on R3's written verification of the new head, with:
   - R3's blocking findings resolved;
   - bounded deletion;
   - the restore gate;
   - the catalog-invariants doc reviewed and acknowledged.
3. **#88:** no wait. The integration owner resolves it during the combine, and T1 (storage and engine writer) reviews that resolution in writing before the freeze (COS decision).
