Restore authority fix handoff

Cause: ProductionRestoreExecutor equated lifecycle grant epoch/lease with accepted placement proof epoch/lease. The real Go issuer uses its current regional lease independently of retained placement authority. Go stats renewal advances observation authority, and lifecycle_operations updates those lease columns; equality against the latest row would also reject legitimate original durable requests.

Fix: retain original signed operation_grant in NativeRestoreRequest and preserve it on renewed RPC retries. Read-only PlacementJournal verifier checks signature, grant structure at issuance, and immutable admitted operation scope/action/placement/canonical restore digest. Executor separately checks lifecycle epoch/lease against the signed grant and accepted placement revision/digest against journal. The digest binds placement epoch/lease, avoiding redundant unsourced placement fields. Restore epoch remains canonical restore request identity. Original authorization remains usable after expiry/observation renewal. No schema rewriting, compatibility shims, or assertion weakening.

Verification: nice -n 19 CARGO_BUILD_JOBS=2 cargo test -p terrabase-server restore --lib: exit101, 13 passed, 3 failed. Changed real-signed placement7/lifecycle8/restore2/renewal9 regression passes, including altered pins/grant, journal reopen. Existing native RPC regression now reopens native SQLite and proves renewed context preserves original request/grant; passes. Existing restore receipt/readback tests pass.

Failures outside changed path: backup.rs1420 snapshot readiness30s; api.rs5227 snapshot readiness30s; backup.rs1502 seed write Deadline73728/100000 refusals0. First two occur before restore; benchmark fails during seeding. No claim of full make check success.

Final nice -n 19 CARGO_BUILD_JOBS=2 cargo test -p terrabase-tbep signed_lifecycle_grant_replays_and_rejects_mutation_after_reopen --lib: exit0,1 passed. Includes unadmitted refusal, signed scope mutation refusal, original grant verification after observation lease renewal. git diff --check passed. All owned test processes terminal. Server initial compiles exposed concurrent import_runtime Other/mut cipher errors, corrected by its owner.

Separate review required; packet agent notified. Resume bounded backup readiness/benchmark investigation under separate ownership; restore fix has no remaining implementation task. Preserve GatedRestore shutdown regression. Future briefs should distinguish restore epoch, lifecycle lease, and placement proof; inspect renewal issuer and journal updates before choosing authority equality.

Source HEAD: a4e79696c0547d08773a456ded9e6d3d34628b12

SHA256:
b6a5dff9765721012f2a9f4698f5ff7f91433b3ef7dc696ab0f3550f5a0e7894  crates/terrabase-server/src/native_recovery.rs
7a5d4e7e9a115ed7cf1e13e9bdf78edd54eaae8c0726c68e9ca832ad3201e9d6  crates/terrabase-server/src/native_recovery_receipt.rs
6d401c227b0cd24a3e6d8a08e0c40f058c1496ad643ef54e4d405d78c64a4b99  crates/terrabase-server/src/production_data_runtime.rs
65a169308ac98b898bcc5e94e93c70f53de88e54a17ca2b1bf92a78a213ab582  crates/terrabase-server/src/production_lifecycle_control.rs
04996c821897037945aa7fe3d4ee11281f814b99a52f9d19ebd74502824814ef  crates/terrabase-server/src/production_data_runtime/transition_retirement_tests.rs
584b5a1e5f1897a078697667f864d18dc0af513a21582591d5906758a81928b6  crates/terrabase-tbep/src/placement.rs
38eb6ea3b67f699ee0716dfe2e7c3fb89870f3b17ad9da4ec9b8ea6c442d9c27  crates/terrabase-tbep/src/placement/transition_tests.rs
