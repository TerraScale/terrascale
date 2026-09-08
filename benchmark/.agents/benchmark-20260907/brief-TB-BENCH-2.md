# Execution brief: wire final TerraBase orchestrator environment

Outcome: the final TerraBase-only runner reaches actual ingest and local
auto-index setup using the fresh database created for each lane. Its real
callers are `run-terrabase-final.sh` and the benchmark binary dispatch in
`bench/src/main.rs`.

Owned writes: `terrabase/bench/scripts/run-terrabase-final.sh` only, including
its shell tests or evidence logs. Do not edit `AGENTS.md`, benchmark result
receipts, product Rust, or `bench/src/*` in this task. The shared script owner
must hand off before another writer edits it.

Design: immediately after `new_database` sets
`TERRABASE_BENCH_TERRABASE_DATABASE`, export the fresh member API and bearer
credential under the names consumed by `auto_index_local.rs`:
`TERRABASE_BENCH_TERRABASE_API="$TERRABASE_BENCH_MEMBER_API"` and
`TERRABASE_BENCH_TERRABASE_AUTH="Bearer ..."` (or derive safely from the
already supplied member values). Before invoking ingest, export
`TERRABASE_BENCH_INGEST_AUTHORIZATION` with the same bearer value. Preserve
the script's immutable artifact, resource PID, fresh database, timeout, and
`nice -n 19` behavior. Avoid broad environment copying or fallback credentials.

Acceptance: with a fresh verified member environment, the auto-index child
must pass configuration and issue its first authenticated request; ingest must
pass health/version/auth setup and either complete or retain a later real
service failure. A deliberately unset credential must fail closed. Verify with
shell syntax checking and a focused dry configuration/reproduction that does
not claim benchmark numbers; the bench_owner performs the actual run.

Forbidden shortcuts: do not weaken auth checks, synthesize receipts, reuse a
stale database, or start another member/build. Any change to public auth
semantics or result schema requires root/Astra review.

Return changed path and exact command/evidence, plus any remaining live-lane
failure. This is routine mechanics and authorized once root assigns the script
owner; no architecture checkpoint is required.
