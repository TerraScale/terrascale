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
| 8 | R2c-2: delete the per-collection change feed and history tables | #88 | `05bd4374` (pushed) | **4** | ready; the integration owner resolves it against `c691f947` during the combine, and **T1 reviews that resolution in writing before the freeze** (COS decision; #88's owner is finished, no separate rebase push) | It shares the feed area with R8 (item 13), so conflicts are expected there. Its format 4 combines with R10a's (see "Storage format"). |
| 9 | R3: CDC change group source | #106 | `4c32da5e` (pushed) | 3 | ready | Head of `2328b8db`, `2ee8b148`, `b838729e`, `81fa4fcb`, `4c32da5e`. |
| 10 | RegionProcesses (regions as OS processes) | #111 | `07f8d81f` (pushed) | none (tests) | ready | |
| 11 | Replicated index DDL; unique re-projection | #112 | `8b8758ff` (pushed) | 3 | ready | |
| 12 | SIGKILL before shipping across processes; lag series | #113 | `b383cb84` (pushed) | 3 | ready | |
| 13 | R8: database changes feed as SSE | #118 | `9277893f` (local; pushed head `90c1b833`) | 3 | ready | `9277893f` adds only the benchmark relabel over `90c1b833`. It sits on #111 and merges into the R3 line. |
| 14 | R8 e2e scenarios | #119 | `90847616` (local; pushed head `56b9ffea`) | none (tests) | ready | Only `ChangesFeedE2ETests.cs`. Its two `[Scenario]` declarations come from the assertion side (item 19). |
| 15 | R6: anti-entropy and refill | #114 | `594b052a` (pushed) | 3 | ready | |
| 16 | Register once (harness for R10a) | #117 | `256d5442` (pushed) | none (tests) | ready | |
| 17 | **R7: garbage collection**, copying refill | #120 | `76cb23ff` (local `feat/r7-gc`; pushed head `811dbf6e`) | 3 (R7 changes no format, #120) | ready | `811dbf6e` → `e8b458e5` stale writes → `7d83cfbf` refill refused → `80c67345` PeerLink abort fix → `baa2e236` harness: nodes register as the first region's user → `76cb23ff` acknowledgement past CDC rows with nothing to ship. |
| 18 | **R10a: catalog as a lattice database** | #116, #121 | R10a's own stack on R7: `a54beab4` (local; `49ec3496` R10a-1, `cdcd019d` R10a-2, `21d05c9c` R10a-3, `a54beab4` e2e) on `baa2e236` | **4** | **WAITING: R10a reports done** (R3's blocking findings resolved, bounded deletion, restore gate) | Not `de0a022f` (#121's pushed head): its registration 500 is known bad. Not R3's withdrawn `e9841c75`/`f82690b6`. Sibling of `76cb23ff` on `baa2e236`, so it merges with item 17. Catalog tombstones stay out of GC (COS decision). Brings `CatalogProcessesTests.cs`; its Gaps flips are the strict owner's. |
| 19 | Assertion side and recorder | #105 + the candidate stack | #105 `4069da9f` (pushed) + local `94086cd6`/`4b85b32c`; the candidate-9 assertion commits on `dea6b364` + R3's `1b68b852` (writer outcomes) | none (tests) | ready | The final `Gaps.cs` follows the candidate stack: it keeps only gaps that still fail their own way. #105's main-only setup-blocked classification is kept only if a scenario still needs it. |

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
2. **R10a:** the stack on R7 reported done, after its gate-blocker fix and R3's verification, with:
   - R3's blocking findings resolved;
   - bounded deletion;
   - the restore gate;
   - the catalog-invariants doc reviewed and acknowledged.
3. **#88:** no wait. The integration owner resolves it during the combine, and T1 (storage and engine writer) reviews that resolution in writing before the freeze (COS decision).
