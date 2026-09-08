# Retained TerraBase performance baseline — 2026-09-07

These are the retained early-session receipts, before final optimized-artifact qualification. They are **diagnostic evidence**, not a qualified before/after benchmark. The common candidate token does not prove an unchanged live database schema or a frozen source-to-executable manifest. No live workload or build was run to prepare this inventory. All original receipts remain intact.

## Served operations

Values are medians across recorded samples, including medians of each sample's latency percentiles (not percentiles pooled across operations). Single-sample rows show that sample. Settings are **seeds / operations per sample / samples / concurrency / warmup operations**. A dash means unavailable, never zero throughput. Every receipt declares `diagnostic-qualification`; `complete` means the raw run completed, not that it passes comparison admission.

| Receipt | Settings | Raw status | Median ops/s | p50 (µs) | p95 (µs) | p99 (µs) |
|---|---|---|---:|---:|---:|---:|
| [terrabase-read-1788824820089556839-1407696-0](results/run-20260907/terrabase-read-1788824820089556839-1407696-0.json) | 1,000 / 10,000 / 5 / 16 / 1,000 | complete | 1,822.212 | 8,538.314 | 13,093.030 | 15,081.951 |
| [terrabase-write-1788825556378152555-1454868-0](results/run-20260907/terrabase-write-1788825556378152555-1454868-0.json) | 1,000 / 500 / 1 / 16 / 16 | failed | — | — | — | — |
| [terrabase-write-1788825949017521963-1478910-0](results/run-20260907/terrabase-write-1788825949017521963-1478910-0.json) | 2 / 8 / 1 / 1 / 0 | complete | 62.120 | 13,395.847 | 27,252.565 | 27,252.565 |
| [terrabase-read-1788825971998574795-1480686-0](results/run-20260907/terrabase-read-1788825971998574795-1480686-0.json) | 100 / 500 / 1 / 2 / 16 | complete | 1,035.680 | 1,739.165 | 2,944.015 | 5,620.563 |
| [terrabase-query-1788825972915051798-1480717-0](results/run-20260907/terrabase-query-1788825972915051798-1480717-0.json) | 100 / 500 / 1 / 2 / 16 | complete | 780.753 | 2,098.729 | 4,891.456 | 6,691.692 |
| [terrabase-mixed-1788825975435334085-1480793-0](results/run-20260907/terrabase-mixed-1788825975435334085-1480793-0.json) | 100 / 500 / 1 / 2 / 16 | complete | 223.244 | 2,811.821 | 26,334.631 | 39,874.154 |
| [Interrupted full write](results/run-20260907/write-interrupted.json) | 1,000 / 10,000 / 5 / 16 / 1,000 | interrupted | — | — | — | — |

The interrupted full write records investigation of unindexed sequence scans; no partial timing was claimed. The 500-operation failed write records `another lane worker failed during the measured window` and zero samples; its [smoke log](/tmp/terrabase-write-indexed-smoke.log) names the same receipt. The tiny eight-write run cannot replace either missing standard-write measurement.

## Exact recorded identity and environment

| Shared field across the six JSON receipts | Recorded value |
|---|---|
| Candidate digest | `43bc943664898c841b48ca490b7ecad31864cfc58721b26e2ff1ac0a2c4d6935` |
| Workload digest | `81b42d494289506cedfd157a8adce10536349e35054fd1a7cf8d639678349cf8` |
| Topology digest | `7c0b5581097a89d270ae9c43ffa59b9547c0a3841f7ca7ed410159e082f50796` |
| Workload / provenance | `terrabase-se15-workload-v1` / `terrabase-runtime-catalog-v1` |
| Controlled host / OS | `mariogk-dev;fenced-member=GRU` / Linux 7.2.3; 28 logical host CPUs |
| Durability | `LocalDurability::Synced acknowledged commits` |
| Payload / addressing | 256-byte seed payload; `seed-{index}` / `user-{index}`; group index modulo 100; read/query index modulo seed count |
| Mixed / writes | Operation index modulo 10: 0–4 read, 5–7 write, 8–9 query; measured write ID `measured-{global operation index}` |
| Noise setting | 5%; a setting does not establish measured stability |

Successful receipts observe healthy GRU `terrabase-0.1.0` at `http://127.0.0.1:7221/api/v1`, with separate database IDs retained in their endpoint fields. Transport is ureq 2 HTTP/1.1, one pooled agent per concurrency slot, Bearer API key, acknowledged JSON operations, and no TLS on loopback. The failed receipt did not capture the server identity. The interrupted JSON lacks the full environment/comparability contract.

| Cohort | Run group | Run digest | Config digest | Recorded window (Unix seconds) |
|---|---|---|---|---|
| read | `e13-local-20260907T2346Z` | `ea657efed19e16281bcdf6aab374a125554d333b04159d2d077249392a63f4b1` | `bdfe342f5a3e96300b3e54f43585ee347bdb8b213b803ea0dac000ef8eb60d80` | 1788824791–1788824820 |
| write | `e13-local-20260907T2357Z` | `57abd07e8d9e0791a5a04f6f498c34636a45fc93733b6253ca05ebcfac3ddd77` | `841a29caa26992806d3c604554b41528a9f9a87cc54cd005da80665fb33c850e` | 1788825556–1788825556 |
| write | `e13-local-20260908T0005Z` | `abddfe0aafca25ba0236fc1501e50769c886e7e1c36cc0d700427f2fdae2cd7a` | `bf76ec64d1e011c6dc17f6e59c7fb41b712b04723dbc71417021076d2a1698b9` | 1788825948–1788825949 |
| read, query, mixed | `e13-local-20260908T0006Z` | `e775c2d9bbf516e0af529e7dfc656a6d8335f2e7815d627e580f96964194679b` | `af4f001f03246809b6a2bc194ba8d29925228daf38d0cc76af94cb47f523227e` | 1788825971–1788825975 |

[member-before.txt](results/run-20260907/member-before.txt) records PID 1404068, cgroup `/system.slice/run-p1404068-i1403054.scope`, CPU quota `200000 100000`, cpuset `0-1`, and memory maximum 12,884,901,888 bytes (12 GiB). Initial memory current/peak is 49,934,336 bytes; RSS is 30,752 KiB. Device `8:0` counters are 20,525,056 read bytes / 17,760,256 written bytes, 267 reads / 1,186 writes. These are a **before snapshot**, not per-workload deltas or measured-window peaks. The six receipts contain no executable SHA-256 or schema hash, CPU model, storage model, or complete concurrent-load trace.

## Local probes and prior engineering logs

| Evidence | Retained observation | Limit |
|---|---|---|
| [Sequence index probe](results/run-20260907/sequence-index-probe.md) | 5,708 facts; watermark VM steps 22,841 → 13, full-scan steps 5,707 → 0; billing VM steps 68,536 → 111, full-scan steps 11,414 → 0 | SQL probe only. Timing 452 → 38 µs / 951 → 65 µs is not served end-to-end improvement; command, schema digest, repetition and host controls absent |
| [Frontier covering-index probe](results/run-20260907/frontier-index-probe.md) | 6,087 returned frontier rows; 520,586 VM steps both before and after; 22.926 → 13.357 ms | Timing differs without reduced VM work; no demonstrated asymptotic improvement or controlled server comparison |
| [Syscall summary](/tmp/terrabase-write-syscalls.log) | strace 7.2 attached to member PID 1404068 with 21 threads; 112,412 calls, 1,025 errors, 16.027039 aggregate syscall seconds; 16 fsync calls | PID matches the member snapshot. Association with the failed smoke is session-reported; trace timestamps and exact command/window are absent, so correlation remains unverified. Tracing perturbs timing; aggregate syscall time is not request wall time |
| [Engine commissioning log](/tmp/terrabase-engine-bench.log) | 256 seeds, 128 warmups, 512 measured operations; three throughput samples: read 4,050 / 4,796 / 3,262; write 45 / 46 / 54; query 3,532 / 3,669 / 3,641; mixed 237 / 238 / 215 ops/s | In-process engineering evidence; no served latency distribution. Three selected tests passed |
| [Materializer log](/tmp/terrabase-materializer-bench.log) | 200 updates; 1k / 10k / 100k source rows; full-scan times 152.952 / 4,099.304 / 76,922.333 ms versus bounded index 32.997 / 23.379 / 19.188 ms; 50 retained rows, 9,500 estimated candidate bytes | Algorithm-specific old artifact; no served throughput claim. One selected test passed |
| [QUIC ACK log](/tmp/terrabase-ack-bench.log) | `compare_default_and_one_millisecond_ack_delay` passed; selected unit-test run 2.31 s | Regression duration is not network latency; filtered binaries with zero tests provide no coverage |
| [100k FTS log](/tmp/terrabase-fts-bench.log) | FTS top-10 27.956705 ms; one selected test passed in 66.96 s | Query diagnostic timing and whole test duration are distinct; not a served query benchmark |

Prior executable paths recorded in these logs: engine/FTS `target/release/deps/terrabase-94c9bf760c69a9e0`, materializer `target/release/deps/materializer_cost-fac1ce3dc669dd40`, and ACK `target/release/deps/terrabase_member_transport-bd21a841ccacd83a`. Build-time source manifests, executable hashes, and original shell exit codes for those old logs are unavailable in the [verification inventory](results/run-20260907/verification.md); current file contents must not be used to invent historical identity. `/tmp` links identify transient evidence and require those files to remain present.

## Before/after claims this evidence cannot support

| Proposed comparison | Assessment |
|---|---|
| Small read/query/mixed diagnostics versus a new standard C16 run | Invalid speedup comparison: seeds, operation count, samples, concurrency and warmup differ |
| Eight-operation C1 write versus standard write | Invalid: tiny zero-warmup smoke cannot measure sustained C16 throughput or tail stability |
| Failed or interrupted write versus a completed optimized run | Completion improvement may be recorded; throughput/latency speedup is undefined because the old measurement is missing |
| Syscall-traced smoke versus an untraced run | Invalid timing comparison: instrumentation perturbs scheduling and syscalls; exact window association remains unverified |
| Standard old read versus standard optimized read | Potential descriptive before/after values only if all settings match; current receipts alone do not prove frozen executable/schema identity, equal contention, or matched experiment controls. Retain diagnostic classification and state confounders |
| SQL probes / engine / materializer / ACK / FTS versus served API | Different operation and measurement boundaries; do not transfer their ratios to API throughput |
| Any TerraBase row versus retained PostgreSQL | [PG audit](postgresql-comparison-2026-09-07.md) admits 0 of 20 receipts; no parity ratio or legitimate numerical PG target |

Final optimized measurements should be appended as separate evidence with their own exact artifact/source/schema identities and settings. This report preserves the early evidence without relabeling it as final-source qualification.
