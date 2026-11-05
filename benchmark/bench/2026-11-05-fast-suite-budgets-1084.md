# Fast suite budgets — issue #1084

All measurements are admitted `ts-heavy-any` jobs on delta, beta or mariogk-dev. Slot admission supplies the CPU reservation required for the timing proof. Full default test parallelism remains enabled. Suite time includes runner and shared-fixture startup and disposal; queue, dependency installation, compilation and release packaging are measured separately. The landing baseline uses its TAP duration; final rows use measured Fast test execution and also report the complete build recipe.

Baseline: core and SDKs `387489c3e8cd46de87b5334f4d864b4fdda2678d`; docs, landing and benchmark `8180e5d9c`. The issue's initial observations were 43 s for account erasure and 10.75 s for graceful-restart incremental pulls; the table uses the once-per-area baseline audit. A ≤ maximum bounds every case by its complete package or runner duration.

| Fast area | Before suite (s) | After suite (s) | Before max (s) | After max (s) | Proof source / admitted host |
|---|---:|---:|---:|---:|---|
| Auth | 34.686 | 7.729 | 15.398 | 2.882 | `843158758`, delta |
| TerraBase + driver | 29.892 | 9.536 | 10.296 | 5.366 | `843158758`, beta |
| Dashboard | 9.935 | 13.507 | 6.488 | 6.926 | `843158758`, delta |
| Libraries | 13.599 | 7.920 | 0.724 | 1.377 | `843158758`, beta |
| SDK .NET (both TFMs) | 11.558 | 7.689 | 8.720 | 0.978 | `7a63f7901`, mariogk-dev |
| SDK JavaScript | 9.056 | 6.386 | 6.839 | 1.304 | `7a63f7901`, mariogk-dev |
| SDK Go | 5.869 | 3.753 | 2.180 | 2.020 | `7a63f7901`, mariogk-dev |
| SDK Python | 4.763 | 1.522 | ≤0.890 | 0.217 | `7a63f7901`, delta |
| SDK Rust | 2.345 | 3.659 | ≤1.020 | 1.021 | `7a63f7901`, mariogk-dev |
| Docs | 1.541 | 0.800 | 0.892 | 1.121 | `843158758`, beta |
| Landing | 0.272 | 0.374 | 0.025 | 0.019 | `7a63f7901`, beta |
| Benchmark | 12.176 | 8.213 | 10.130 | 4.460 | `843158758`, mariogk-dev |

The after columns are the final completed shipping proofs. On its clean rebase, standard ts-ship carried unchanged SDK/Landing receipts from `7a63f7901` to `843158758` and reran the affected areas. All required statuses and release artifacts were qualified for the shipped revision. The independent Docs/benchmark repeat proofs below used identical committed test bodies, verified by SHA-256. Every row is below both caps. The five-repeat isolation proof below is independent of these final shipping runs. Maxima also conservatively include separately printed release-qualification test durations, which can exceed the primary Fast phase wall because they are different executions. Dashboard repeats its Fast cases during release qualification; when available the larger whole-runner duration bounds every case in that rerun. Landing’s final complete build recipe took 11.643 s; the table uses the measured test phase. Benchmark uses individual Go JSON case timings from the final admitted shipping run. An earlier shipping run took 15.552 s with a 12.937 s whole-package duration. A structured admitted follow-up exposed two sequential parent tests at 11.730 s and 10.100 s; that candidate is rejected as per-case timing proof. The remaining phase-window, ramp-completion and safety-guard subcases now run in parallel with their existing independent fixtures, assertions and temporary files. JavaScript retains its pre-existing optional live-server skip; no new skips were introduced.


## Five-repeat proof

Every case passed, with zero skips, in every repeat. Builds completed once before the repeats; runtime processor counts and default TUnit parallelism were left unchanged. TerraBase's independent core and driver assemblies ran together. Each repeat includes runner/shared-fixture startup and disposal.

| Area | Repeat | Cases | Suite wall (s) | Maximum case (s) |
|---|---:|---:|---:|---:|
| Auth | 1 | 30 | 8.464 | 4.601461 |
| Auth | 2 | 30 | 7.300 | 2.789891 |
| Auth | 3 | 30 | 8.599 | 4.437532 |
| Auth | 4 | 30 | 8.310 | 3.555850 |
| Auth | 5 | 30 | 9.298 | 5.490871 |
| TerraBase + driver | 1 | 61 | 8.447 | 4.860789 |
| TerraBase + driver | 2 | 61 | 7.245 | 3.232820 |
| TerraBase + driver | 3 | 61 | 7.280 | 3.193908 |
| TerraBase + driver | 4 | 61 | 8.489 | 5.137278 |
| TerraBase + driver | 5 | 61 | 7.355 | 4.186055 |


Auth source `e82ec484fc204fa5c8bb6f8a8c8d1c215cbd66a4`, admitted delta: five complete 30-case repeats passed without skips. The two additional moved Slow classes passed independently: tenant classes 3/3, 5.189 s runner; closure replay 1/1, 4.145 s runner. The complete closure file is byte-identical to its preceding Fast source, and the tenant matrix method body is identical.

## Baseline cases at or above eight seconds


| Area | Test | Baseline (s) |
|---|---|---:|
| benchmark | `TestSummaryMustMatchFullWorkload` | 10.130 |
| benchmark | `TestPitrLifecycleWritesCutsAndVerifies` | 8.930 |
| sdk-dotnet | `EveryOperationSendsItsRecordedRouteAndEveryRouteIsUsed` | 8.720 |
| sdk-dotnet | `Every_public_source_route_has_an_SDK_call_and_no_route_is_dropped` | 8.141 |
| terrabase | `Sql_tables_restore_to_a_point_in_time_with_their_rows_schema_constraints_and_triggers` | 10.296 |
| terrabase | `A_restored_database_is_a_new_incarnation_and_a_replica_of_the_source_rebootstraps_from_it` | 9.888 |
| auth | `Users_exchange_their_sign_in_for_organization_and_project_tokens_their_role_allows` | 15.398 |
| auth | `Deleting_an_account_deletes_its_own_organizations_leaves_shared_ones_and_frees_the_email` | 14.059 |
| auth | `Refresh_replay_ends_only_the_compromised_grant_across_warm_and_fresh_instances(False)` | 13.982 |
| auth | `A_user_enrols_signs_in_with_the_second_factor_uses_a_recovery_code_once_and_turns_it_off` | 13.016 |
| auth | `An_invitation_is_emailed_works_once_for_its_invitee_and_is_audited` | 11.902 |
| auth | `Viewers_read_admins_disable_and_outsiders_see_nothing` | 11.271 |
| auth | `Roles_gate_every_change_and_an_organization_keeps_an_owner` | 11.143 |
| auth | `Signing_out_everywhere_ends_every_device_session_and_token_family` | 11.071 |
| auth | `End_users_sign_in_to_a_project_client_and_get_tokens_only_the_project_issuer_accepts` | 10.297 |
| auth | `A_project_key_is_shown_once_and_exchanged_for_a_project_token_with_the_contract_claims` | 9.729 |
| auth | `An_owner_creates_renames_and_deletes_an_organization_and_outsiders_see_nothing` | 9.322 |
| auth | `A_confirmed_change_moves_sign_in_to_the_new_address_and_ends_every_session` | 9.043 |
| auth | `End_users_sign_up_and_back_in_with_the_projects_provider(google)` | 8.586 |

## Changes and retained coverage

Auth shares assembly-owned TerraBase and auth hosts with separate journey users, organizations, projects and databases. Recipient-specific asynchronous delivery signals keep parallel email flows isolated. Management setup seeds real stored sessions and obtains tokens over HTTP with PKCE; dedicated hosted sign-in tests retain the complete sign-in coverage. Clock, key-mutation, delivery-provider and replica journeys retain their required isolation. Ordinary signing keys use the production cache; rotation tests explicitly request fresh reads. MFA, revocation, email change, scopes, cross-tenant refusal and access-token validation retain their assertions.

TerraBase retains 57 Fast cases plus four native-driver cases after moving 18 additional complete heavy critical cases unchanged to Slow. These include JetStream local writes and cut-link replay, graceful/encrypted/catalog restart, replica-status, cross-region listing, rewind, filtered shape lineage and flight/grant drills. It shares ordinary management/SQL/page-sync hosts and SQL setup authority while each journey owns its database or tenant. Snapshot-sealer and host-wide metric tests own their host. Both independent Fast assemblies run together and retain their failure and timing guards. Native driver initialization happens once in assembly setup. Streaming fixtures generate the same-sized random text rows from random byte buffers, avoiding millions of per-character random draws alongside the parallel suite. Graceful-restart data shrank to 76 rows, preserving acknowledged-write durability; the complete drill now runs in Slow. The Fast encrypted-replica fixture seeds the same 100 rows in one transaction before its separately asserted incremental changes. Ordinary WebSocket CRUD and nonmutating token-expiry validation also share the assembly server. TTL expiry uses fake time and an awaited maintenance pass. Focused cross-tenant read/write/management refusal and authenticated WebSocket CRUD stay in Fast; their complete original isolation/protocol matrices stay in Slow.

The .NET SDK route inventory returns a recorded HTTP 400 rather than a transport error that triggers retry delays; it still checks every route and now requires one request per operation. Dashboard awaits RPC listener readiness. Independent benchmark summary/ramp subtests run in parallel. JavaScript package/declaration qualification remains unchanged in its separate Slow command and in release qualification. The final audit also exposed a 20.910-second isolated Go module consumer; its full archive, file-proxy and isolated module-cache test body now runs unchanged in Slow and release qualification. SDK basic, SQL, auth and CLI flows remain in Fast.

Heavy critical erasure/restart, three-replica authentication, replica consistency, PITR/restore, WAL retention, clock safety, atomic home apply, SQL transport deadline, crypto erasure and CDC retention drills moved intact to Slow. A later admitted auth run still passed all 49 cases at 27.299 s while 17 complete security journeys took 14–18 s. Those MFA, global revocation, email change, customer-project sign-in/refresh, SSO, device approval/replay, invitation/roles, broad exchange permissions, password/reset and replica drills now run unchanged in Slow. The later tenant user/session matrix and closure lost-answer replay also moved unchanged after a strict repeat measured 12.095 s and 10.830 s; their focused client/settings and social sign-in paths remain Fast. At the five-repeat source Fast retained 30 cases and Slow 32, preserving all 62 cases present before these moves (584-case policy baseline). The later independent upstream policy commit `ae878b183` deleted the noncritical host collectability test; final auth Fast has 29 cases. No Keep-list item lost coverage. No test limiter, skip or ordering dependency was introduced. JavaScript retains its pre-existing optional live-server skip.

## Independent Slow proof

Auth at `f55a23337`, beta, one test per class, zero skips: erasure 8.031 seconds, deletion handoff/restart 4.694 seconds, three-replica replay 14.773 seconds. The initial missing-executable setup failure and its correction are recorded in #1084; the corrected command built the same-commit TerraBase executable. At `a70ab375d`, mariogk-dev, all four further full drills passed with zero skips (runner times): three-replica token validation 13.096 seconds, restart credential survival 15.204, cross-replica sign-in revocation 16.470, social lifecycle 19.019. Each full file was byte-compared against its original Fast source before the change.

TerraBase SQL restore/rebootstrap at `73b650fe9`, beta: 2/2 passed, zero skips, runner 5.092 seconds. At `e727713d4`, mariogk-dev: clock 2/2 (13.240 seconds), home apply 3/3 (11.798), crypto erasure 2/2 (5.235), WAL 1/1 (5.136), socket deadline 1/1 (9.039). These are complete runner durations, and all nine cases passed without skips. At `9bbbac19e`, mariogk-dev: replica anti-entropy 1/1 (5.697 seconds), cross-region metering 1/1 (8.689), regional shape tail 1/1 (10.478), three-node token validation 1/1 (6.442). All four passed with zero skips; each moved file is byte-identical to its original Fast version. CDC retention at `094b4be9c`, mariogk-dev: 1/1 passed without skips, runner 22.215 seconds; its complete multi-consumer, backup and push-floor drill also moved unchanged.

At `1697bb40f`, beta, the four additional TerraBase cases passed with zero skips: complete RPC management (4.206 s), operator/discovery contract (2.974 s), database lifecycle (2.188 s), and restricted-JWT refusal matrix (2.193 s). These are individual case times from TRX; locked restore of the added Slow RPC client dependency also passed.

Benchmark full PITR lifecycle at `07fcd06f4`, mariogk-dev: 1/1 passed, zero skips, case 9.170 seconds. JavaScript packed consumer at `d3f6424cc`: 1/1 passed, runner 5.270 seconds, plus successful typecheck.

Go isolated module consumer at `7e73b913d`, beta: `bash sdk/go/scripts/test-slow.sh` passed with zero skips, root package 12.090 s including race instrumentation. Its body was byte-compared with the prior Fast version after removing only the Slow build constraint. Go Fast passed 113 test/subtest results in 2.784 s, maximum 2.010 s.

At `e92e39b11`, delta, all 17 additional auth security cases passed independently in Slow across 11 class runs, with zero skips. Each moved test method was byte-compared with its previous Fast version.

At `47ee2a785`, admitted delta, all 18 further moved TerraBase cases passed independently in Slow across ten class runs, zero skips. Maximum case: rewind 3.1908871 s; JetStream local-write/cut-link cases 2/2, maximum 2.5474384 s; graceful-restart incremental pulls 1/1, 1.9234928 s. The other full replica, encrypted/catalog restart, cross-region listing and shape flight/grant/filter cases all passed. Eight entire files are byte-identical to their preceding Fast source; the six methods split into two additional files were byte-compared unchanged. Core Fast 75 → 57; driver Fast remains four cases.

## CI repair and shipping

Auth whitespace is fixed on main by `1eb73330d77d47db5f9f335876d6ea191fbd57e0`; [CI 41913887159](https://github.com/TerraScale/terrascale/actions/runs/41913887159) passed. The original `b230bd274` [.NET proof rerun](https://github.com/TerraScale/terrascale/actions/runs/41910292588) and [Dashboard Fast rerun](https://github.com/TerraScale/terrascale/actions/runs/41910292497) passed. Changed C# files passed `dotnet format whitespace --folder --verify-no-changes` before pushing.

Implementation tip: `84315875862c9a981a08fd05a67f61189ada645c`, shipped by normal ts-ship. The final three area commits are `2e028b550` (libraries), `9b6be39dc` (benchmark) and `843158758` (Docs). The tracking issue records the report commit after shipment. All implementation commits contain DECISION lines citing #1084.

Final proof log: `/home/mariogk/.local/state/ts-ship/20261105T000604-b3ebb092.log`. Final source proof rows are retained in `wrk1084-shipping-rows.json`.

The tracking issue retains failed/interrupted candidate evidence. Those attempts do not count as the accepted timing proof. Temporary warning gates remain owned by the skills/CI lane, which can make the 10 s case and 30 s suite caps hard after this issue closes.


Dashboard admitted candidate `32ce10eaf` passed all 112 assertions but failed the case budget at 10.086 s for the full HTTP/MCP restore drill; other heavy recovery/erasure cases reached 9–10 s. That attempt is rejected timing proof. The complete HTTP/MCP restore, cross-replica snapshot restore, cold-restart durability, backup pagination and six cross-subject erasure matrix cases now run unchanged in Slow (Fast 112 → 102, Slow +10). SDK/MCP basic backup creation/restore, account sign-in, tenant refusal and billing remain Fast.


The library endpoint and logging assemblies now run together, retaining both exit codes and their existing 92 cases. The final normal ts-ship run requalifies every area for its exact revision. The direct `5c1ced123` push initially lacked Docs, Dashboard and SDK Go receipts; normal ship `843158758` supersedes it. Docs and Dashboard Fast CI passed at that revision. SDK Go Fast is dispatched on qualified main because the library Makefile and Go README changes do not trigger its path-filtered push workflow. Further pushes use ts-ship only.


## Final maximum cases

| Area | Maximum case / conservative bound | Seconds |
|---|---|---:|
| Auth | `Scalar_renders_the_contract_without_browser_errors(Light, 1440, 900)` | 2.882 |
| TerraBase + driver | `A_runaway_tenant_is_interrupted_and_throttled_while_another_tenants_reads_stay_fast` | 5.366 |
| Dashboard | `AnalyticsExchangesFreshProjectAuthorityAndRejectsInvalidTokensWithoutLosingSession` | 6.926 |
| Libraries | `OpenApiUsesTheFinalRouteAndEachMethod` | 1.377 |
| SDK .NET (both TFMs) | `Every_file_operation_matches_the_pinned_producer_route_and_scope` | 0.978 |
| SDK JavaScript | `replay-safe reads retry transient refusals 429 tenant_busy reads wait for Retry-After and succeed` | 1.304 |
| SDK Go | `terrascale.tech/sdk/TestPostgresTLSBeforeStartup` | 2.020 |
| SDK Python | `tests.test_client::test_config_validation` | 0.217 |
| SDK Rust | `v1::client::token_tests::placement::placement_matches_recorded_contract` | 1.021 |
| Docs | `renews early, denial never falls back to stale cache, explicit later retry coalesces` | 1.121 |
| Landing | `en: all five plans have coherent prices, cadence, and allowances` | 0.019 |
| Benchmark | `terrascale.tech/ts-benchmark/internal/benchmark/TestMultipleClientRamps/one_cleanup_fails` | 4.460 |


Dashboard’s ten newly moved Slow cases passed on admitted delta at `ede432661`, zero skips: cross-subject erasure 6/6 (max 2.588 s), snapshot restore 1/1 (2.428 s), HTTP/MCP restore 1/1 (3.025 s), cold restart 1/1 (2.930 s), backup pagination 1/1 (2.670 s). These maxima round up from TRX. All five edited files were compared with their originals after removing only the inserted Slow category attributes.


The first post-quota shipping candidate `7a63f7901` passed assertions but failed the TerraBase case budget: suite 28.481 s, maximum 13.549 s (`A_deployed_function_inserts_and_selects_through_sql_over_https_and_logs_the_run`), admitted mariogk-dev. It is rejected timing proof. The later final source includes upstream replication changes `7056f0a12` and is measured separately; no discarded run is excused as contention. A preceding candidate could not publish statuses because the GitHub REST quota was exhausted until 03:05:31 UTC; it was not shipped.


Docs audit candidate `9385e943b` passed all assertions but its portability case took 13.046 s (14.337 s suite), so it is rejected timing proof. The test now reads 32 independent file pairs together rather than serially reading all 2,162 files. Every filename and every byte comparison remains. Five admitted delta repeats passed all 23 Node and five Python checks: suite 0.768, 0.848, 1.135, 1.254, 0.753 s; maximum case 0.616 s.


The remaining benchmark subtest groups passed three admitted beta repeats after adding parallel execution only: suite 10.278, 9.859, 5.928 s; maximum case 4.720, 5.170, 3.030 s, respectively. All 178 Go test/subtest results passed in each repeat. The fixture and assertion bodies remain unchanged.

## Follow-up: the 16.542-second TerraBase observation

The 75-case candidate `e82ec484f` took 25.823 seconds and had a 16.542-second maximum for `A_cut_link_loses_nothing_a_pull_while_pending_applies_nothing_twice_and_redelivery_applies_once`. It is rejected timing proof. The complete multi-host JetStream local-write/cut-link drill subsequently moved unchanged to Slow in `01ce035ae` (rebased move source `47ee2a785`). Its independent Slow run passed both cases without skips, maximum 2.5474384 seconds, as recorded above. No assertion or recovery coverage was deleted.

The restarted admitted proof at `99c979f19` passed the hard gates with 57 TerraBase Fast cases plus four driver cases: 8.285 seconds suite wall, maximum 4.291 seconds. The same audit passed all 12 areas' Fast timing checks. Docs and Dashboard subsequently failed artifact publication because their exact-source release tags already existed; those are failed overall qualifications, despite successful Fast phases. Their qualification helpers now verify an existing release's successful exact-revision proof and archive integrity before reusing it, after completing the fresh audits and tests. Published assets are never overwritten. The Docs archive also identifies the source in both built revision files; Dashboard binds every application file and its qualification log through hashes.

## Hard-gate follow-up after qualification repair

The requested restart passed all 12 Fast timing gates at `99c979f19`. The two affected qualification helpers then shipped through normal ts-ship at `32aa154a4f153902115eebe8df95255a7b079fdc`; Docs and Dashboard reran for that exact revision. The other ten areas have identical source across this rebase. Each area uses one admitted ts-heavy-any job, full default parallelism, and the enforced 30-second suite / 10-second case gates. The maximum column conservatively includes separately executed release tests; Dashboard uses its complete 9.402-second release runner as an upper bound.

| Area | Fast wall (s) | Maximum case / bound (s) | Source |
|---|---:|---:|---|
| auth | 13.321 | 4.739 | `99c979f19` |
| terrabase | 8.285 | 4.291 | `99c979f19` |
| dashboard | 12.738 | ≤9.402 | `32aa154a4` |
| libs | 6.938 | 0.504 | `99c979f19` |
| sdk-dotnet | 7.067 | 0.994 | `99c979f19` |
| sdk-js | 5.475 | 1.275 | `99c979f19` |
| sdk-go | 3.367 | 2.010 | `99c979f19` |
| sdk-python | 1.453 | 0.170 | `99c979f19` |
| sdk-rust | 3.198 | 1.020 | `99c979f19` |
| docs | 0.663 | 1.128 | `32aa154a4` |
| landing | 0.223 | 0.016 | `99c979f19` |
| benchmark | 8.443 | 4.430 | `99c979f19` |

The accepted qualification repair retains every fresh audit, Fast run, formatting check and runtime smoke. Reuse requires a successful exact-source release proof from the existing trusted publisher plus the verified published artifact. Twelve local fixture controls cover valid reuse and refusal of wrong revision, corrupt checksum, failed proof, untrusted proof and changed source/log identity; an admitted beta job verified both actual published artifacts using the final validators. The first candidate requiring identical bytes from two fresh site builds was rejected after discovering nondeterministic site output, and its superseded Dashboard job was stopped by its exact owned key. No published asset was overwritten.

Final code qualification log: `/home/mariogk/.local/state/ts-ship/20261105T005800-b2746046.log`. Original all-area restart log: `/tmp/wrk1084-restart-proofs.log`.
