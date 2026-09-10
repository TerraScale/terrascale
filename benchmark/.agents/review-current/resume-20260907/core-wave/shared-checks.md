# Shared Rust check lane (2026-09-07)

All Cargo and Make commands from this lane used nice -n 19 and build concurrency 2. Existing active processes were preserved. No product source edits or commits were made.

Evidence directory: `/tmp/terrabase-rust-checks-20260907`.

| Check | Terminal result | Evidence |
| --- | --- | --- |
| `make check` | exit 2 at formatting | `resume-make-check.log`, `resume-make-before.json`, `resume-make-after.json` |
| Current engine compile | exit 0 | `resume-engine-build.log`, source before/after hashes |
| Engine corrected selectors | supervisor 3, admission 1, staging 1, split runtime 2 passed | `resume-engine-artifact-results.json`, `resume-engine-artifact-*.log` |
| New server selectors | webhook 32; native recovery 9; readiness 1 passed | `resume2-server-artifact-results.json`, `resume2-artifact-*.log` |
| Runtime substring | 37 passed, 1 ignored, auth seed failure | `resume2-artifact-runtime__tests.log` |
| Import diagnostics | 2 passed, 2 failed with engine Conflict | `resume3-import-diagnostics.log` |
| Official contracts generator | exit 101, missing HostEngine method | `resume-contracts.log`, source before/after hashes |

The runtime substring includes auth_runtime tests. The failure is `auth_runtime::tests::lost_consume_response_replays_the_same_public_operation`, auth_runtime/tests.rs:1115: seed returns 503 auth_authority_unavailable instead of 200. Reproduced alone (`resume-auth-repeat.log`). Runtime-specific new regressions passed.

Both import failures are actual engine Conflict: HTTP import and pending-before-submission restart replay. The import owner traced the fixture engine origin epoch to zero and requested explicit fixture activation; correction verification remains pending.

The contracts command failed at `crates/terrabase-tbep/src/engine_host.rs:367`: `HostEngine` has no `shard_admission_for_key` method. No new producer bundle was produced.

Source changed during compilation. Engine owned paths remained unchanged in its before/after window; server output is artifact-specific evidence rather than a claim about every latest dirty file. The second server artifact came from the preserved independent `/tmp/terrabase-performance-verification-20260907/debug-build.jsonl` compilation, whose binary listed the new recovery bounds, import pending replay, and runtime cleanup regressions before execution. Hash identities are recorded in the artifact JSON files. Rust/proto/Cargo/Make inputs are recorded by `hash-current.py`.

Formatting and strict lint debt remain; no green workspace make check or production readiness claim. No new production member binary was built for Firecracker.
