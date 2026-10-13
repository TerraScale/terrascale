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

## MERGE TRAIN PAUSED (17:30Z): CI is disabled

**State:** the CI workflow 381575024 is `disabled_manually` by the user (the "Reduce Blacksmith CI Runner Costs" thread,
to stop Blacksmith spend). #139 (merged 17:29Z → main `b25787f5`, outside this train) moved CI to GitHub-hosted
`ubuntu-latest`, but CI stays disabled, so `b25787f5` has **no run**, and no PR can get a `test` check on its exact
head. Main's `ci.yml` still has both gates (preview L259, deploy L315, `if: false &&`).

**No merges or closes** until the user re-enables CI and the new runners prove green on main.

**Local runs are not a substitute.** Owners keep preparing and testing locally under `flock -o /tmp/ts-heavy.lock`;
those runs are evidence only and never replace the required CI `test` check on the exact head.

**Waiting for CI:**
- **#112** `81172462`: R3 merged main in, and it is clean. No run yet.
- **#118** (R8): conflict resolution pending; T1 reviews it.
- **#134** (Dogfood): conflict pending.
- **#127**: auth failure.
- **#125**: fresh run.

**Also seen on main:** `c59bfcc5`'s `auth` job failed on logging tests
(`Console_records_are_json_and_withhold_exception_messages`,
`ZLogger_entries_keep_values_and_exception_messages_out_of_every_provider`). Routed to Dogfood.

## CI runners, the backup program, and acceptance gaps (COS update, 2026-10-12 evening)

**CI (corrected ~18:45Z).** The CI workflow 381575024 is **active again**: the alpha owner re-enabled it around 18:40Z,
and runs happen on PRs again (#127's at 18:43Z; #142's runs failed). **The train restarts on the first green CI run on
main.** Main is `4cfbf85d`, which has no run yet; the last main run, `c59bfcc5`, failed on the auth logging tests routed
to Dogfood. PRs still need a green run on their exact heads.
- The **alpha owner** (thread mcp:4c7de8fc…, owner of CI edits) runs five ephemeral self-hosted runners:
  - **#142** (monorepo, `47ed19f7`) "ci: run on the alpha self-hosted runners";
  - **ts-dotnet #9** (`4e561130`).
- **No green proof yet:** the first cold run, 38291185189 on `ac98c679`, **failed** libs, auth and terrabase; alpha is
  correcting it. There is no full-green or warm run yet.
- **The train stays paused** until alpha's runners give a real green on main (now `4cfbf85d`) and then on each PR's
  exact head. Current-head tests and reviews remain the merge gates. The deploy, preview and cleanup pauses are
  unchanged.
- **Zero-config concern:** COS verified #142's merge tree. It sets `runs-on: ${{ vars.CI_RUNNER || 'ubuntu-latest' }}`
  on every job, which is a repo-variable runner switch. COS relayed the concern to the alpha owner through the
  coordinator; it is not ours to edit. Both holds are intact in that tree (preview L276, deploy L340 `if: false &&`).

**T1's #143** (`a67349bf`, base main): test-only write-ahead-log sync faults in E2E (H4). T1's `_system` FULL PR comes
next; it is not opened yet.

**Beta 100M backup program** (a user-authorized separate program; lead mcp:742c4b1c…).
- It owns the **local** branch `fix/backup-recovery-integration`, from `4cfbf85d`: restore proof, shipper backlog and
  retry, multipart snapshots, committed-write acknowledgement.
- **Integration order:**
  1. T1's #143 and the `_system` FULL PR first.
  2. Then beta's candidates, rebased onto main, with T1's written acceptance (and R10a's for the backup-bucket anchor).
- Beta's work enters this train only once it opens PRs.

**Fixes for main's red go first** (like #115):
- **#141** (ts-auth SafeLogging pooled-state bug; fail-first 3/3 → 5/5) fixes `c59bfcc5`'s auth logging failures.
- **#138** (TerraBase binds its own ports) fixes the auth 0 ms fixture failures.
- Both were brought up to date with main `4cfbf85d` at about 18:50Z via update-branch: #138 → `05ea346f`, #141 →
  `95157ef7`. Push check passes. #138's earlier green run 38282945774 (17:15Z, old runners, old main) does not count.
- They merge once green on their exact heads. Then main's push run must be green before the rest of the train.

**Push-check fix (18:52Z):** `push-check.sh` printed PASS for a commit it could not resolve (an empty diff). It now
fetches first and fails closed on an unknown commit. Every earlier check ran on a commit that was already present
locally (fetched or built here), and #105's and #108's merge trees matched COS's independent verification.

**Merged (fixes for main's red):**
- **#141** at `95157ef7`: `test` 38293613049 success (auth). Squash → main `2cfd1df2`; push run 38295445221, deploy and
  preview skipped.
- **#138** at `05ea346f`: `test` 38293602375 success (auth). Squash → main `1721bff1`; push run 38295480962, deploy and
  preview skipped.
- Main's run on `1721bff1`: pending. It must be green before the rest of the train.

**#127 (Aspire) MERGED outside the train's hand.** Merged by **MarioGK** (the shared account; not this integration
owner) at 19:13:16Z, at its exact head `fd80c0a5` → main `6bb896bb`. `test` run 38294328854 on `fd80c0a5` succeeded
(libs, auth, terrabase); preview and deploy were skipped.

Tester A's review at `f0b58ec4` and what happened to each point:
- **Should-fix #1 is NOT addressed (flagged).** "ts-auth's localhost fallback isn't limited to Development."
  - On main, `src/TerraScale.Authentication/App.cs:38` still maps `FLY_APP_NAME` null or empty to
    `http://127.0.0.1:8081` **in every environment**.
  - The fix commits (`8cd4cd23`, `fd80c0a5`) only changed `localhost` to `127.0.0.1`.
  - So an off-Fly ts-auth that is not in Development stores to whatever listens on 127.0.0.1:8081, where before #127 it
    failed closed.
  - It needs a follow-up fix from #127's owner, relayed by the coordinator: the same condition as TerraBase (Development
    and off Fly), failing closed otherwise.
- **The auth readiness failure is not reproduced, and not explained.** `Readiness_treats_a_hung_authority_as_not_ready`
  failed on run 38276106422 (`f0b58ec4`). The final head's auth passed (one run).
  - Tester A noted that #127 changes what the bootstrap hits off Fly: `127.0.0.1:8081` is refused at once, where the
    old Fly DNS name failed to resolve. A first-log ordering race is not excluded.
  - It stays a watch item. A recurrence is a race in that test to fix, not a flake.
- Points 3 (per-run data directory) and 4 (scaffold leftovers): `8cd4cd23` says "data directory emptied at start, no
  ESLint", and its diff shrinks `aspire/package.json`. Point 2 (no local token flow) is a README note. Not
  re-verified line by line.

**Main push runs after #141, #138 and #127. Fast is GREEN; Slow has NO RESULT (infrastructure):**

| Main | Run | libs | auth | terrabase | test | slow |
| --- | --- | --- | --- | --- | --- | --- |
| `2cfd1df2` (#141) | 38295445221 | success | success | success | success | failure, no result |
| `1721bff1` (#138) | 38295480962 | success | success | success | success | failure, no result |
| `6bb896bb` (#127) | 38296187467 | success | success | success | success | failure, no result |

Main's auth red (`c59bfcc5`) is fixed: auth passes in all three runs.

Every `slow` job ended "failure" about 17 minutes in, with the same pattern:
- the `docker/build-push-action` step **never concluded** and no step failed;
- the job's log is gone (`BlobNotFound`), and no test result was reported;
- the job runs on GitHub-hosted `ubuntu-latest` with `timeout-minutes: 60`, so it is not the job timeout.

**Cause (COS, from job 114973317488's runner annotation): "No space left on device"** during the Slow Docker build, on
GitHub-hosted `ubuntu-latest`. Main's `ci.yml` has no `CI_RUNNER` yet, because #142 is unmerged. **Slow is unproven**,
and the release gate "Slow green on main" stays open. The CI owner fixes the Slow runner's disk (relayed via the
coordinator).

**COS DECISION (19:5xZ):** the train **resumes** on exact-head green Fast checks.
- Every merge made while Slow is unverifiable is marked "Slow unverifiable" in its record.
- The final acceptance still requires a green Slow on main's head.

**Order:** #112 → #118 → #134 → #125 → #143 → #144 …, each with a fresh run on current main.
- #112 → `99cb927b`, #143 → `e3800ec7` and #144 → `4a61561b`, brought up to date via update-branch after the push
  check; their runs are in progress.
- #118 (R8) and #134 (Dogfood) still conflict, and their owners were nudged.
- #125 is a draft with its owner.

**T1's #144** (`46cb6806`, base main): "the system database syncs every commit (decision 0012)", the `_system`
`synchronous=FULL` PR. It merges after #143, per the agreed order.

**Merges after the resume (Fast green on each exact head; Slow unverifiable on main):**

| PR | Head | `test` run | Method | Main after | Push run | deploy / preview | Slow |
| --- | --- | --- | --- | --- | --- | --- | --- |
| #112 | `99cb927b` | 38300110257 success | merge (stack base) | `79d30e8b` | 38302155744 | skipped / skipped | unverifiable |
| #143 | `e3800ec7` | 38300119319 success | squash | `51341591` | 38302187019 | skipped / skipped | unverifiable |
| #144 | `4a61561b` | 38300128698 success | squash | `1e421848` | 38302222222 | skipped / skipped | unverifiable |
| #113 | `1fa6a0c5` | 38302286695 success | merge (stack base) | `4afd7d9d` | 38304582048 | skipped / skipped | unverifiable |
| #114 | `2e01953e` | 38312656763 success | merge (stack base) | `afb82f95` | 38314496504 | skipped / skipped | unverifiable |
| #146 | `b5fe4d4a` | 38312666751 success | squash (closes #127's should-fix #1) | `62d7448a` | 38314532241 | skipped / skipped | unverifiable |

#114 is retargeted to main, merges cleanly, and is updated to `980be395`; its run is in progress. #117 and then #120 follow (R3 pushes the R7 pin `24e138c0` to #120). Main's push runs after #112, #143 and #144: `test` success; Slow pending or unverifiable.

**#134 (Dogfood) and #146 (ts-auth: off Fly, only Development stores in the loopback TerraBase).**
- **Tester A's independent reviews:** #134 at `c1645986` and #146 at `2ef197fc`, **no blocking findings**. The fail-first
  tests were verified with mutants, and local `make check` exited 0 on both.
- **Their exact-head runs were green** (#134 38301093027, #146 38298854993). They ran on GitHub-hosted runners, and COS
  decided that the exact-head `test` check counts whichever runner executed it.
- **Fresh runs:** main had moved since (#112, #143, #144, #113), so both were brought up to date via update-branch after
  the push check: #134 → `4b6b5093`, #146 → `d67259d7`. They merge on a fresh green run, marked Slow unverifiable.
- **Nits are non-blocking.** Dogfood may fix the api.md "refuses any other pair" wording in a follow-up.
- **#146 appears to be the follow-up** for #127's should-fix #1 (the loopback fallback limited to Development off Fly).
  To be confirmed in its diff before it is recorded as closing that finding.

**#134 MERGED by MarioGK (the shared account, not this integration owner)** at 21:44:19Z, at its exact head `4b6b5093`
(`test` 38305114053 success) → main `2ae35da0`. Slow unverifiable.
- **#114** (green on `980be395`, run 38304631863) and **#146** (green on `d67259d7`, run 38305120063) were green on the
  previous main, and #146 also touches ts-auth.
- So both get fresh runs on `2ae35da0`: #114 → `2e01953e`, #146 → `b5fe4d4a` (update-branch, push check passed).
- **#146 verified to close #127's should-fix #1.** Off Fly, ts-auth uses `127.0.0.1:8081` only in Development; any other
  environment throws `AuthConfigurationException` and refuses to start (fail closed), and the storage client is resolved
  at build time.

**#117** is retargeted to main, merges cleanly, and is updated to `e161a139`; its run is in progress. Then R7's #120 follows (R3 pushes `24e138c0`).

**MAIN IS RED AGAIN (22:45Z): #149 was merged with a RED exact-head `test`.**
- **The merge:** #149 ("health probes name the exact commit and machine") was merged by MarioGK (the shared account,
  not this integration owner) at 22:08:08Z, at head `27858113`. Its own `test` run 38311725043 was **failure**, which
  breaks the merge gate.
- **The damage:** main's run on `2a3eeb14` (38314879993) failed 13 Fast Healthz/Readyz tests, all
  `ArgumentNullException (stringValue)` in `HealthTests.AssertProbeAsync`: `Healthz_reports_ok_and_the_running_version`,
  `Readyz_is_ready_on_the_admin_listener`, `Readyz_is_unavailable_once_the_storage_engine_is_closed` and others.
- **#117 is collateral, not the cause.** Its fresh run on `5c791aac` (38316832119) failed the same tests.
- **The train holds product merges** until #149's owner fixes main and main's push run is green.

**Other merges outside the train since 21:44Z:**
- #150 (agent docs) → `90cf9046`;
- #118 (R8) → `71585005`, merged by MarioGK at 22:37:57Z at head `8ebcbd60`. Its storage-side review was done by a
  reviewer "standing in for T1" at 22:29Z, not T1 itself;
- #151 (machine-pinned E2E identity) → `e30ffdc2`.

Main is now `e30ffdc2`.

## Stack integration plan and review status (00:15Z, COS request)

**Corrections to COS's picture:**
- **#114 (R6) is already merged.** It merged at its exact green head `2e01953e` → `afb82f95`, with a merge commit.
- **#117 is already retargeted to main.**
- **`mergedBy=MarioGK` appears on every merge,** this owner's included, because the account is shared. "Outside the
  train" means only that this integration owner did not run the merge.

**#118 was merged against CHANGES REQUESTED.** The storage-side review (independent, standing in for T1, comment
6133818433) requested changes at `8ebcbd60` at 22:29:09Z. #118 merged at that same head at **22:37:57Z**. So **B1, the
silent event loss when a new origin's events are trimmed, is on main.** **#165** (R8's B1 fix) is the fix-forward and
gets priority right after main is green; its last run was cancelled.

**Main is red since #149** (merged with a red exact-head `test`). **#166** ("Docker test stage gets TERRABASE_REVISION";
touches `ci.yml` and the `Dockerfile`; the push check keeps both gates; 0 threads) is the fix and **merges first** once
its exact head is green.

**#166 is red on its exact head** `48429edc` (run 38323041647). The #149 Healthz/Readyz tests now pass, but 3 terrabase
Fast tests fail out of 1433:
- `Output_budget_includes_escaped_ids_row_wrappers_and_envelope(_id, padding)`;
- `Deleted_documents_are_not_selected_counted_or_indexed`;
- `A_catalog_change_between_flush_admission_and_write_keeps_the_batch_for_retry(fields)`.

Not merged. The owner is asked to name the cause: this change, or pre-existing failures that main's runs could not show
while Healthz failed first. Main stays red.

## ORDER CHANGE (COS, ~01:30Z): R7 out of the critical path

**Why:** #120 (R7 GC)'s independent review requested changes, with **data-loss must-fixes** (comment 6135823941). R7
moves to a new owner, **R7-FIX** (bare branch `fix/r7-gc-safety`), and #120 merges **last**, after R7-FIX and a
re-review. **R10a re-stacks without R7.**

**New order, once main is green** (same gates: exact-head Fast green on current main, every review and comment read and
any must-fix blocking, Slow unverifiable, nothing pushed to a PR whose base is not main):
1. **#166.** Main's red; currently red on its exact head, owner asked for the cause.
2. **#165.** R8's B1 fix.
3. **#117.** Harness, register once.
4. **#116 (R10a-1).** Retarget to main once #117 merges; R10a pushes its re-stacked head only after the retarget.
5. **#121 (R10a-2, re-stacked on main).** Retarget to main once #116 merges. R10a sends the SHA and a range-diff, then
   pushes. T1 reviews the storage-format merge (main is already at version 4).
6. **The R10a-3 plus gate PR,** opened with base main after #121 merges (branch `feat/r10a-3-catalog-gate`).
7. **#119.**
8. **#152,** after R3's must-fixes and a re-review.
9. **#162**, then **#163.**
10. **R8's repair-feed PR,** after #165. It depends on the R6-FIX PR for #114.
11. **The R6-FIX PRs** for #114 and #112.
12. **The #106 fix-forward.**
13. **#164**, then **#145.**
14. **The AUTH-REGIONAL PR.**
15. **#120, last,** after R7-FIX and its re-review.

This replaces the earlier exact order (fa4ff0a8) and R10a's R7-based placement. The approval through `dd98ebba` was on
R7, so the re-stacked heads need the range-diff and a confirming review.

**Review gate (COS, 00:25Z; every PR, test-only included):** before a merge, every review and comment on the exact head
is read.
- Any CHANGES REQUESTED or must-fix from any reviewer blocks the merge until a re-review on a newer head approves.
- Each merge records the reviewer's verdict and comment URL.
- `train-merge.sh` refuses formal `CHANGES_REQUESTED` reviews; free-text must-fix comments are read by hand.
- The independent Opus reviews COS commissioned: #166 (first), #165, #120 (R3's `24e138c0` against main), #152, #162,
  #163, #164, and retro reviews of #106, #112 and #114.
- **Retro fix-forward findings on #106, #112 or #114 become new PRs ahead of #152 in the queue.**

**Exact order** (each step: exact-head Fast green on current main, all findings handled, Slow unverifiable; nothing is
pushed to a PR whose base is not main):
1. **#166.** Fixes main's red. Then main's push run must be green.
2. **#165.** The B1 fix for #118, already on main. Needs an independent storage review.
3. **#117** (harness, register once; on main): a fresh run on green main, then a **merge commit**, since it is the base
   of #120 and #116.
4. **#120 (R7):** retarget to main after #117 merges. Then R3 pushes the pin `24e138c0` plus origin/main to
   `feat/r7-gc` (allowed once its base is main). Merge commit.
5. **R10a in three PRs** (R10a's placement, #108 comment 6134782297; independent approval through **`dd98ebba`**):
   - **#116 = R10a-1.** Its head `757ec6c4` has the same patch ID as R10a's `305890b3`, so it is ready as is. Retarget it
     to main after #120 merges, bring it up to date with main, give it a fresh run, then a merge commit.
   - **#121 = R10a-2,** to be **replaced by `2334a941`** (R10a-2 rebased onto R7, format 4). #121's current `de0a022f`
     is not rebased. Retarget #121 to main after #116 merges; then R10a pushes `2334a941` plus origin/main. T1 reviews
     the storage-format merge: main is already at version 4 after #88, so `--storage-format` is re-recorded.
   - **A new PR for R10a-3 plus the gate** (`a0b86d7d..dd98ebba`, branch `feat/r10a-3-catalog-gate`), opened with base
     main only after #121 merges.
   - **R10a's order:** main ← #117 ← #120 ← #116 ← #121 ← the R10a-3/gate PR.
   - **#120's R7 fixes:** R3's pin `24e138c0` already contains `e8b458e5` and `7d83cfbf` (verified; also `80c67345`,
     `baa2e236`, `76cb23ff`). Only #120's pushed head `811dbf6e` lacks them, so pushing `24e138c0` plus main brings
     them in.
   - **No push to a PR whose base is not main.** The previews org has 18 of 20 machines, so such a push could create
     a preview app, not just fail with 422.
6. **#119** (R8 e2e, on main): a fresh run on green main. Its last failure was the backup flake that #128 fixed.
7. **Main-base candidates:**
   - #152 (R3; R-C, R-D, R-B), then **#162**, which is stacked on #152 and retargeted only after #152 merges;
   - #163 (change log retention by epoch), #164 (pinned runs and backup qualification), #145 (Dogfood negative-claim
     tests).
   - #152's red is collateral from main's red (Healthz).
8. **#125** (CI optimizer): still a draft and conflicting.

**Independent reviews, per PR:**

| PR | State | Independent review |
| --- | --- | --- |
| #106, #111, #112, #113, #114 (R3, R6) | **merged by this owner** on exact-head green and no open findings | **none on record.** The train's written gate was exact-head green plus findings; COS's guardrail also named required independent reviews. **Recommended:** a retroactive storage review of #106, #112 and #114 (CDC source, replicated DDL and unique, anti-entropy and refill) on main. |
| #118 (R8) | merged outside the train | **CHANGES REQUESTED (B1), merged anyway**; fix-forward is #165 |
| #117 (harness) | open | none (test harness only) |
| #120 (R7 GC) | open (stacked) | **missing**: storage review of `24e138c0` |
| #121 / #116 (R10a) | open (stacked) | **APPROVED** round 2 at `0facba2f` (comment 6134686131, on #108). The 2 nits need a check that the final head changes only them. |
| #165 (B1 fix) | open | **missing**: storage review |
| #152, #162, #163 (R3) | open | **missing** |
| #164 | open | **missing** |
| #166 (CI fix) | open | CI owner (alpha) or COS |
| #145 (Dogfood) | open | Tester A COMMENTED (19:54Z) |
| #119 (R8 e2e) | open | test only |

**Acceptance gaps, kept visible until each has a test and a fix:**
- **R-A:** stale-machine retention after a SIGKILL.
- **R-B:** a corrupt CDC entry stalls a region pair.
- **R-C:** an unknown CDC entry is silently skipped.
- **Unexplained `/metrics` first-scrape hangs.**

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

**#133 supersedes #132** (the integration owner's call, revised 16:00Z after the user's order to close genuinely
replaced PRs). #133's first commit `97ed1d7c` is byte-identical in content to all of #132 (`833d21e2`, the three
contract test files), so #133 contains everything #132 delivers.
- #132 is closed **only after #133 has merged**, with comments linking both PRs and the preview-workflow check recorded.
- If #133 stalls, #132 merges on its own instead.

**Six distinct product Fast flakes** are seen so far, the five above plus
`Declared_JSON_schema_and_native_uniqueness_coexist_without_changing_collection_identity` on #132's run 38270496291.
All are routed to T1.

- **#115 MERGED** at its exact head `93fe0a56`: `test` run 38274256511 success (libs, auth, terrabase); 0 open threads.
  Merge commit → main `df790dbb`; push run 38274891319, deploy and preview skipped, both gates present; preview
  workflow 381612805 re-verified `disabled_manually` at 16:04:49Z. **Main's run on `df790dbb`: everything green, Slow included** (auth, terrabase, libs, test, slow). So #102, #103, #104 and #68
  now pass on main, and main is green again. The train resumes.

**Fresh runs on main `df790dbb` (16:15Z).** Each branch was brought up to date with `gh pr update-branch` (a merge
commit from main; no force push), after its push check passed and the preview workflow was re-verified disabled:
- #88 → `8122d5d3`;
- #106 → `36de93da`;
- #127 → `f0b58ec4`;
- #133 → `2719d3d8`;
- #130 → `03115e4c`;
- #134 → `f6aff9b5`;
- #79 → `a18c40e9` (merged by hand; a `benchmark/bench/README.md` row conflict, both rows kept).

**Planned merge order once each is green:**
1. #130, then a fresh run on #125;
2. #133, then close #132 as superseded;
3. #88;
4. #106, then retarget #111 and its stack;
5. #127;
6. #134;
7. #79.

**Merges 16:20–16:27Z** (each with a fresh green `test` on its exact head over `df790dbb`, 0 open threads, and the preview
workflow re-verified `disabled_manually` by the helper):

| PR | Head | `test` run | Method | Main after | Push run | deploy / preview | Main run `test` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| #130 | `03115e4c` | 38276120332 success | squash | `2ca15d6c` | 38276914039 | skipped / skipped | pending |
| #133 | `2719d3d8` | 38276112293 success | squash | `d271c079` | 38276954117 | skipped / skipped | pending |
| #88 | `8122d5d3` | 38276092544 success | squash | `bd1c4efc` | 38277036473 | skipped / skipped | pending |
| #106 | `36de93da` | 38276100821 success | squash (**error: should have been a merge commit**, as the base of R3's stack) | `0acfee13` | 38277073606 | skipped / skipped | pending |
| #79 | `a18c40e9` | 38276207869 success | squash | `c379cc3b` | 38277117579 | skipped / skipped | pending |

- **#132 closed** as superseded by #133. Proof: two files are byte-identical on main, and the third is #132's version
  plus 197 lines with 0 deletions.
- **#106 squash, impact:** merging main into #111 is clean (merge-tree reports no conflict), and so is
  `rebase --onto main 4c32da5e`. The rest of the R3 stack gets merge commits.
- **#111 is retargeted to main.** R3 merges main in, or gives a written OK for update-branch.
- **Main's storage format is now 4**, after #88 (R2c-2). R10a-2 also bumps to 4; the combined Id comes from
  `--storage-format`.
- **#134 conflicts with main** after #133. Back to Dogfood, since it is product code.
- **#127 is red:** auth Fast `Readiness_treats_a_hung_authority_as_not_ready` (run 38276106422). Back to its owner.
- **#125 needs a fresh run** now that #130 has merged. Its owner merges main in.

**Main after these merges:**
- **#130's run** (`2ca15d6c`): test and Slow success.
- **#133's run** (`d271c079`): `test` **failed**. Auth Fast's shared TerraBase child failed to bind `127.0.0.1:37391`
  ("address already in use"), so 277 auth tests failed at 0 ms. That is a free-port race in the auth test harness,
  intermittent: later runs passed auth. Dogfood is diagnosing.
- **#88's, #106's and #79's runs** (`bd1c4efc`, `0acfee13`, `c379cc3b`): `test` success, but **Slow failure**,
  #135/#136 `Public_count_excludes_the_overflow_row_whether_or_not_it_matches(False|True)`.
  - Cause: #88 removed `collections.generation`, and #110's Slow `CountBoundTests` inserted the old 4-value row.
  - **Fix: #137** (test only, one line), 4/4 CountBoundTests on mariogk-dev.
  - No further product merges until Slow is green on main again.

- **#137 MERGED** at its exact head `4187637d`: `test` 38281182568 success (terrabase). Squash → main `dab85174`; push run 38281713863, deploy and preview skipped. **Main's run on `dab85174`: auth, terrabase, test and Slow all success.** Main is fully green, and product merges resume.

- **#111 MERGED** at its exact head `9d32cf1e` (the pin `07f8d81f` plus a merge of main): `test` 38281114070
  success, 0 threads. **Merge commit** → main `c59bfcc5`; push run 38282841575, deploy and preview skipped.
  - #112 and #118 are retargeted to main.
  - #112 merges cleanly; R3 brings it up to date.
  - #118 conflicts with main in `api.md`, `DocumentsExtensions.cs` and `TerraBaseJsonContext.cs` (#88's feed deletion
    against R8's database feed). R8 resolves it, and T1 reviews the resolution in writing.

**Not merged until fixed and verified:**
- **R10a's stack**, until the no-peer fail-open and the unprotected override are fixed and R3 verifies in writing;
- **#125**, until its metrics race is fixed (#130) and a fresh `test` run on its exact head is green;
- anything whose Slow or metrics fix is still open.

**No PR is closed.** Closing destroys its preview app, which is the user's pending cleanup decision.

## Open PR overview (user order 15:51Z; baseline 15:59Z)

**Baseline: 21 open in the monorepo, plus ts-dotnet #5.**

| Count since the baseline | Value |
| --- | ---: |
| merged | 9 baseline PRs (#115, #130, #133, #88, #106, #79, #111, #127, #112) + #113, #134 (merged by MarioGK), #114 + new PRs #137, #141, #138, #143, #144, #146 |
| closed (proven superseded) | 1 (#132, by #133; 16:23:01Z, preview workflow `disabled_manually`) |

The 9 merges before the baseline are in the merge-train table.

**Preview workflow check:** workflow 381612805 (`previews.yml`) is `disabled_manually`, re-verified at 15:59Z. It is
checked again before every close or merge, because no preview deletion is approved.

| PR | Owner | Base | Head | Class | `test` on head | Waits on |
| --- | --- | --- | --- | --- | --- | --- |
| #88 | (finished; T1 reviews) | main | `05bd4374` | ready | success (old base) | main green, a fresh run |
| #106 | R3 | main | `4c32da5e` | ready | success (old base) | main green, a fresh run; the R3 stack's base |
| #127 | Aspire owner | main | `c8424758` | ready | success | main green, a fresh run; COS check (another thread's PR) |
| #133 | R3 | main | `79ebd2c2` | ready | success | main green, a fresh run; supersedes #132 |
| ts-dotnet #5 | Dogfood | main (ts-dotnet) | `4827b03e` | ready | success | #133 (the SDK side of the regional contract) |
| #79 | strict owner | main | `d9f56bf5` | failing | failure (product Fast flake) | main green, a fresh run |
| #115 | T1 | main | `93fe0a56` | **merged** → `df790dbb` | success (38274256511) | — |
| #119 | R8 | stacked on #118 | `90847616` | failing | failure (backup flake, fixed by #128) | its stack, then a rerun |
| #125 | CI optimizer | main (draft) | `6d384fab` | failing | failure | #130 merged, then a fresh run |
| #130 | metrics owner | main | `ee4d132e` | failing | failure (old-base flake) | main green, a fresh run |
| #132 | Tester C (via strict owner) | main | `833d21e2` | proven superseded by #133 | failure (product flake) | closed after #133 merges |
| #134 | Dogfood | main | `29928db1` | pending | not run yet | its run, main green |
| #108 | strict owner | stacked on #114 | `7dc901e9` | dependent | success | the R3 stack; then replaced by a base-main assertion-side PR |
| #111, #112, #113, #114, #117 | R3 | stacked (→ #106) | as pinned | dependent | success (old bases) | #106 merged, then each retargeted bottom-up |
| #118, #119 | R8 | stacked on #111 | `9277893f`, `90847616` | dependent | success / failure | #111 merged, then retarget |
| #120 | R3 (R7) | stacked on #117 | `811dbf6e` (pin `24e138c0`) | dependent | success (old head) | #117 merged; R3 pushes `24e138c0` |
| #116, #121 | R10a | stacked on #117 / #116 | `757ec6c4` / `de0a022f` (pin is the next R10a head) | dependent; **blocked** | success (stale heads) | R10a's catalog-open fix, R3's written verification, #120 merged |

**Owner estimates:** requested 16:00Z from T1 (#115 and the Fast flakes), R10a (restore gate), R3 (the stacks, #133) and
Dogfood (#134, ts-dotnet #5); recorded here as ranges when they answer.

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
| 17 | **R7: garbage collection**, copying refill | #120 | **`24e138c0`** (R3's local R7 tip; supersedes `76cb23ff`, which it contains) | 3 (R7 changes no format, #120) | **OUT of the critical path**: CHANGES REQUESTED with data-loss must-fixes (comment 6135823941); R7-FIX owns `fix/r7-gc-safety`; merges last | Contains `e8b458e5` (stale writes), `7d83cfbf` (refill refused), `80c67345` (the PeerLink abort fix), `baa2e236` (register as the first region's user), `76cb23ff` (ack past CDC rows with nothing to ship) and the Fast restarted-machine wait. R10a's stack sits on it. |
| 18 | **R10a: catalog as a lattice database** | #116, #121 | **`dd98ebba`** (approved; on R7 `24e138c0`; split into #116 = R10a-1 `305890b3`, #121 = R10a-2 `2334a941`, and a new main-base PR for `a0b86d7d..dd98ebba`): `305890b3` R10a-1, `2334a941` R10a-2 (format 4), `a0b86d7d` R10a-3, `959904dc` e2e, `f03c62e7` invariants, `db573c42` bounded delete, `84dff445` fail-closed restore gate, `a9c56e8f` QuietAsync routing, `60eae912` restore-gate blockers (no opening on an empty peer list; explicit genesis bootstrap; no unauthenticated override), `33306cdf` operator catalog-open, `9608a2ea` per-machine verification and restore anchor, `7d3f0eec` revocations anchored before they are acknowledged, and pulls counted only from trusted peers, `79ecf7c2` only commits that revoke access wait for the anchor, `f2c32f99` a missing anchor keeps a non-genesis catalog untrusted, `c3fda431` adoption runbook and `/catalog-gate` reporting pending and digest, `feec75e5` anchoring decided by the routed endpoint's metadata (R3's blocker), `7af6811a` every mutating write classified and non-DELETE revocations anchored, `817f7897` a backup destination anchored only when it replaces one | **4** (`4-44d5cdc5b81c` alone, per R10a) | **APPROVED through `dd98ebba`** (independent round 2, #108 comments 6134686131 and 6134782297) | R10a's results at `817f7897`: Fast 1400/1400; E2E 30 passed + 29 landed, 3 skipped, 1 failed. The failure is the single-region stall test; per R3 it needs R3's bootstrap one-liner plus the strict owner's `f8972480` (assertion side, item 19: the stall scenarios use the harness's in-process stall) in the combine; format `4-44d5cdc5b81c` (unchanged). The failure is the single-region storage stall, whose harness now needs a catalog bootstrap; that harness change is asked of R3. #116 and #121 are retargeted to main only after their base PRs merge (#120 → #117 → #114 → … → #106). R10a then brings both to the current head and runs the `.github` check before pushing. |
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
2. **R10a:** the catalog-open fix is done at `9608a2ea` (`33306cdf` operator catalog-open, `9608a2ea` per-machine verification and restore anchor). Superseded by `7d3f0eec` (revocations anchored before they are acknowledged; trusted-peer pulls). Superseded again by `f2c32f99` (`79ecf7c2` only commits that revoke access wait for the anchor; `f2c32f99` a missing anchor keeps a non-genesis catalog untrusted). Superseded again by `feec75e5` (`c3fda431` adoption runbook and `/catalog-gate` reporting; `feec75e5` anchoring decided by the routed endpoint's metadata, R3's blocker). Superseded again by `817f7897` (`7af6811a`, `817f7897`). The stack waits on R3's written re-review of `817f7897`, with:
   - R3's blocking findings resolved;
   - bounded deletion;
   - the restore gate;
   - the catalog-invariants doc reviewed and acknowledged.
3. **#88:** no wait. The integration owner resolves it during the combine, and T1 (storage and engine writer) reviews that resolution in writing before the freeze (COS decision).
