# TerraBase benchmark preparation — 2026-09-07

Inventory is source/evidence only. Benchmark execution and all shared Cargo
builds belong to `bench_owner` at `nice -n 19`; this preparer does not run or
modify the product, benchmark drivers, or human-owned instruction files.

| ID | Observable outcome | Source / evidence | Dependencies | Write owner | State |
| --- | --- | --- | --- | --- | --- |
| TB-BENCH-1 | Produce one fresh, auditable TerraBase-only run covering point read/write/query/mixed, ingest, auto-index diagnostic, and sync; retain raw logs, receipts, member evidence, and manifest. | `bench/scripts/run-terrabase-final.sh:9-115`; `bench/FAMILIES.md`; existing failed/incomplete `bench/results/run-20260907/verification.md` | Verified release server artifact, `local-member.sh env`, 2 OCPU/12 GiB cgroup evidence; no concurrent member/build interference | bench_owner: `bench/results/run-*` | ready, execution lane active elsewhere |
| TB-BENCH-2 | Make final orchestrator pass the environment required by the local auto-index and ingest drivers. | `run-terrabase-final.sh:40-41,64-76`; `bench/src/auto_index_local.rs:29-33`; `bench/src/ingest.rs:353-372` | TB-BENCH-1 harness source freeze; root review of shared script change | worker: `bench/scripts/run-terrabase-final.sh` | ready, unassigned |
| TB-BENCH-3 | Render a readable TerraBase-only Markdown comparison from the fresh receipts, with diagnostic/unavailable limits explicit and no comparator rows. | `bench/src/history.rs`; `bench/RESULTS.md`; `bench/FAMILIES.md` | TB-BENCH-1 receipts and report schema | report owner: new `.md` under `bench/` | ready, report lane active |
| TB-BENCH-4 | Diagnose and correct any remaining run failures from the fresh manifest, preserving failed attempts and rerunning only affected lanes after source/build handoff. | Previous failures: `bench/results/run-20260907/verification.md`; current member process snapshot | TB-BENCH-1 failure logs; server source freeze; one shared build owner | assigned failure worker by root; benchmark reruns bench_owner | blocked on fresh evidence |
| TB-BENCH-5 | Keep historical engine diagnostics accounted for without mixing them into the served-lane comparison: engine/auto-index, materializer, ACK, FTS, and restore selectors remain separately labeled. | `/tmp/terrabase-bench-inventory.txt`; `/tmp/terrabase-engine-bench.log`; `/tmp/terrabase-materializer-bench.log`; `/tmp/terrabase-ack-bench.log`; `/tmp/terrabase-fts-bench.log`; `/tmp/terrascale-restore-redundant-index-benchmark.log` | No shared build until bench_owner schedules; restore has a historical threshold failure | bench_owner owns artifact selection; report owner owns Markdown | ready, unassigned |

Shared build resource: one `terrabase` release build and one `bench` release
build at a time. The existing `/tmp/terrabase-benchmark-20260907` member and
its cgroup are active; do not stop or reuse it without bench_owner's handoff.
The separate `/home/.../terrabase/bench/.member` member is also running and is
not valid as an isolated comparison subject while the benchmark member exists.
