# Scan diagnostic evidence — 2026-09-24

Eight Firecracker diagnostic lanes passed: one SQLite and one embedded TerraBase scan for each of four 1,024-row shapes. They used the same immutable, feature-enabled musl executable, SHA-256 `2d513de890316c07fc7ace9e1ba28687bf901c9042e0206a15b8766bce6c6592`, saved as `target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline-scan-2d513de8`. Its adjacent `.source-sha256.txt` records the **build-time** source inventory. The receipts report benchmark HEAD `5685f163bb7e9339e8eb0c839fe2512aaf562648` and TerraBase HEAD `352d9f61185d1b685e3032519579040d169ad47d`; both working trees were dirty, so those commits alone do not identify the compiled source. In particular, the binary embeds driver source `379aeb13…`, adapter `cc286b65…`, and storage `local.rs` `ca70ec32…`. Later retained-WAL lookup work in `local.rs` is a separate source stage and is **not** represented by these scans.

Receipt directories below are relative to `bench/firecracker-harness/`; each successful directory contains `run-manifest.json` with `status: passed` and `guest-output.tar.gz` with `receipt.json`, samples and guest logs. The SQLite and TerraBase receipts in every pair report the same fixture SHA-256, final-state SHA-256, row count and revision sum. All completed one scan with zero reported errors.

| Shape | SQLite receipt | TerraBase receipt | Fixture SHA-256 | Final SHA-256; rows | TerraBase physical fixture |
| --- | --- | --- | --- | --- | --- |
| P03, 256-byte body, checkpoint | `p03-sqlite-1024-256-checkpoint-ctime-20260924-b/` | `p03-terrabase-1024-256-checkpoint-ctime-20260924-b/` | `a5951121…` | `992ce8f3…`; 1,024 | one immutable run, checkpoint sequence 8, zero mutable bytes |
| P04 empty, mutable only | `p04-sqlite-1024-tiny-noforce-ctime-20260924-c/` | `p04-terrabase-1024-tiny-noforce-ctime-20260924-b/` | `1d5c1a3d…` | `e3b0c442…`; 0 | zero runs, checkpoint sequence 0, 287,744 mutable bytes |
| P04 empty, checkpoint | `p04-sqlite-1024-tiny-checkpoint-ctime-20260924-b/` | `p04-terrabase-1024-tiny-checkpoint-ctime-20260924-b/` | `1d5c1a3d…` | `e3b0c442…`; 0 | one immutable run, checkpoint sequence 16, zero mutable bytes |
| P04 sparse tail, mutable only | `p04-sqlite-1024-tiny-sparse-tail-ctime-20260924-b/` | `p04-terrabase-1024-tiny-sparse-tail-ctime-20260924-b/` | `1d5c1a3d…` | `d2741db8…`; 1 | zero runs, checkpoint sequence 0, 287,608 mutable bytes |

The old diagnostic executable (`terrabase-sqlite-baseline-profile-6a8f17ae`, SHA-256 `6a8f17ae03d2c02b641c3f3ae2b016a71548fd696394c1ed69557046abf7c979`) and its receipts remain unchanged. Old and new TerraBase receipts match the fixture and final-state hashes and the physical fixture facts above. Their measured **storage work** changed as follows; these are counts for one scan, not elapsed-time estimates:

| Shape | Run file opens | Run block bytes read | Next-key records examined | Mutable keys examined | Point-lookup calls |
| --- | ---: | ---: | ---: | ---: | ---: |
| P03 checkpoint | 32 → 1 | 9,470,592 → 295,956 | 3,224 → 210 | 0 → 0 | 16 → 0 |
| P04 mutable empty | 0 → 0 | 0 → 0 | 0 → 0 | 525,824 → 1,024 | 1,024 → 0 |
| P04 checkpoint empty | 2,049 → 1 | 123,772,928 → 60,436 | 1,048,576 → 2,048 | 0 → 0 | 1,024 → 0 |
| P04 sparse tail | 0 → 0 | 0 → 0 | 0 → 0 | 524,800 → 1,024 | 1,024 → 0 |

The prior TerraBase receipts are `p03-terrabase-1024-256-checkpoint-diag-20260924-a/`, `p04-terrabase-1024-tiny-noforce-diag-20260924-a/`, `p04-terrabase-1024-tiny-checkpoint-diag-20260924-a/`, and `p04-terrabase-1024-tiny-sparse-tail-diag-20260924-a/`, in table order. The new eight receipts use `CLOCK_THREAD_CPUTIME_ID` and contain available `cpu_runtime_ns`, `cpu_bracket_elapsed_ns`, and `cpu_bracket_overhead_ns` fields. The old `/proc/self/schedstat` CPU deltas have no verified comparable clock qualification. The new bracket overhead includes diagnostic probes and scheduling, and one-operation CPU/latency values have no repeated-trial interval. No speed win is claimed from them. The failed mismatched-runner directory `p04-sqlite-1024-tiny-noforce-ctime-20260924-b/` is preserved and has no successful guest receipt; the matching replacement is the `-c/` directory above.

The corrected storage `scan_contract` suite also passed 7/7 in Firecracker at `engine-final-scan-contract-testhooks-5302-20260924-a/`, including a self-reexecuted 128-run case that asserts guest `RLIMIT_NOFILE=64/64`. Its guest test executable SHA-256 is `530297b3633ffac6003b522d7f88e5ae94de4603448938eba9a184f707973b72`; the source-inventory file SHA-256 is `89d82189db6cc7744488249bf6cb42f3267f37068a2fb986aa29e5d168ba8878`. That fixture revision is later than the scan executable's inventory; the measured storage implementation hashes match. The earlier failed fixture receipt remains separate.

The registered 50k/100k-row cases, 8-KiB bodies, hot/cold cache states, repeated paired trials, and resource/SSD gates remain open. These small diagnostics establish matched logical results and the measured reduction in scan work for the four stated shapes only.
