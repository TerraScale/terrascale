# Astra correction supplement: harness fix compile blockers

Batch: `benchmark-20260907-harness-fix-correction-1`
Prior packet: [index.md](index.md), batch `benchmark-20260907-harness-fix`
Prepared: 2026-09-08 UTC

## Delta and source state

This supplement covers the tiny correction delta currently present in the dirty
terrabase worktree. The complete authored diff is
[correction-harness-fix.diff](correction-harness-fix.diff). It removes imports
that are already supplied by `super::*` in the commit timing probe test and
updates a comparison to match the `RegionCode` return type of the owner API.

| Repository | Prior reviewed source | Current source | Changed files |
| --- | --- | --- | --- |
| terrabase | prior harness packet source; stale release evidence was diagnostic | HEAD `4a2c36aabdc5cbd663d20f4dbe8d5cedb1a48fc7` plus dirty delta | `M src/engine/commit_timing_probe.rs`, `M src/engine/shard_split.rs` |

Current SHA-256 identities (captured after the concurrent shard catalog worker
advanced the same file):

* `src/engine/shard_split.rs`: `755797ecfc21abdca253db61051ba793db4716e1a97369fdbbbc06fdae4873e2`
* `src/engine/commit_timing_probe.rs`: `1e32eebad6c9e94c98824863d802fa7fea4c5e32e98ded5048a57dc1143edbcf`
* `bench/scripts/run-terrabase-final.sh`: `4eb0272d912a988f7028911da7b38ef311a21ba1bb41bcea204066bfc74ddad6`

These hashes and the packet files are verified in [SHA256SUMS-correction](SHA256SUMS-correction).
No product files were edited by the packet preparer. The current
`shard_split.rs` also contains a concurrent, excluded catalog addition,
`shard_file_id` removal, and formatting around lines 840-957; those changes are not part of
this correction diff and must receive their own review ownership. The only
correction hunk in that file is the `region()` comparison now around line 934.
The full live `git diff` remains available to Astra for interaction review.

## Caller contract

At `src/storage/ownership.rs:152-154`,
`DatabaseOwner::region(&self) -> RegionCode` is a `const fn` returning the
value by copy. `DatabaseOwnerHandle::region` in `src/member/mod.rs:480-482`
has the same `-> RegionCode` contract. Therefore
`self.database_owner().region()` is a `RegionCode` and dereferencing it is a
compile error. The changed caller is `Engine::file_shard_identity` at
`src/engine/shard_split.rs:917`, where the decoded `identity.region` is also
compared as a `RegionCode` and must retain the conflict check.

The removed probe imports are unused because `use super::*` imports the parent
module's names. This is a mechanical compile cleanup; no runtime behavior or
benchmark contract is intentionally changed.

## Verification state and limits

| Check | Evidence | State |
| --- | --- | --- |
| Source diff | `git diff --check` on the current dirty slice | exit 0 at packet preparation; current source hash is recorded above |
| Rust compile | `.agents/qualification/core-test-compile-corrected-20260907.log`, `.agents/qualification/core-check-corrected-20260907.log` | stale failures from an earlier source state, not valid evidence for this delta |
| Benchmark release | `bench/results/run-20260907-independent-1714381/` | stale diagnostic/failed point attempts; no passing qualification claim |

The build owner is expected to run the applicable focused compile/check after
the source handoff. Astra can review this source delta while that evidence is
pending, but the correction is not verification-complete until a fresh command
records exit status against these exact hashes. Reuse of the prior harness
review remains valid for unchanged script code; changed Rust callers require
this delta review.

## Reading order

1. [correction-harness-fix.diff](correction-harness-fix.diff)
2. `src/storage/ownership.rs:148-154` and `src/member/mod.rs:478-482`
3. `src/engine/shard_split.rs:900-920`
4. `src/engine/commit_timing_probe.rs:1-12`
5. Prior [index.md](index.md) for harness and benchmark evidence chain

No correctness approval is made by this packet.
