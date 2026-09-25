# D02 scan diagnostic summary

Status: **diagnostic complete**. This is an instrumented TerraBase-only scan observation. It is not an a2 measurement or a performance verdict; it has no SQLite lane, ratio, confidence interval, or acceptance classification.

The guest ran the frozen D02 executable (SHA-256 08876c79991489c3727afb12e07802927d839f0dde341160a2c1ce16143067d4), bound to source manifest e6a239c190286259c0aecd855e23b0570b1ce9bda833b26dc9583f4d5476b4a6, 81-file inventory deeffbbbc7b1559de4b47c286a904bee64967f20a0d012f66bd8c3da2b9303f7, source delta 88198cfd293d895a49d6830f9b5c83eb781e2e997716984adc95c20fb5b5a5c7, and protocol 468f86c2a8989ea2c054203f997ce546feb107ab2ac2f443ff6f629491840cc1. The payload artifact map is SHA-256 9f29c29b9a7060b8c3b5c53f00bccf6cbf208ab1b5ebc07f12ff164b372c19a3; execution addendum is a29d4471faf5f8fb4f12fd0be5fb0cf9ac0c656d20c329f8de7a0b402c4fe31b. The parent a2 ELF (93e2099f…) is provenance only and was not executed here.

The fixture contains 1,024 one-byte rows with seed 1, all rows deleted and checkpointed. Its SHA-256 is 1d5c1a3d24146cea8f09abafd72ce11bbedbac9722c3fb40e3cc9ae46f0504d5. The guest completed one first_after_fixture scan and 32 ordered warm scans. All 33 slots succeeded as empty scans with checksum zero; there were no failed or unrun slots.

The phase table describes the 32 warm slots only. Means are arithmetic means. For an even count, the displayed median is the upper of the two central observations, matching the generated report's convention. Values are nanoseconds.

| Measurement | n | Min | Mean | Median | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| Operation wall (inclusive) | 32 | 748345 | 1220904.000 | 1278686 | 1423602 |
| Storage::scan_d02 wall (inclusive) | 32 | 747513 | 1219590.375 | 1277264 | 1422173 |
| Prelude admission/allocation (exclusive bracket) | 32 | 15272 | 25691.250 | 22846 | 102191 |
| Cursor open/directory (exclusive bracket) | 32 | 20591 | 34260.719 | 34542 | 40075 |
| Initial advance (exclusive bracket) | 32 | 83356 | 161048.188 | 154449 | 235157 |
| Merge and later advances (exclusive bracket) | 32 | 552824 | 973182.781 | 1015590 | 1174594 |
| Block load (nested; non-additive) | 32 | 79731 | 155100.719 | 146864 | 229197 |
| Whole-operation thread CPU | 32 | 750621 | 1219481.844 | 1263126 | 1413960 |

The prelude, cursor-open, initial-advance, and merge brackets are the disjoint coarse wall phases. Storage::scan_d02 and operation wall are inclusive measurements. Block-load time is nested inside advance/merge and must not be added to the exclusive phase totals. Thread CPU covers the whole operation and cannot be apportioned among the phases. The 64 empty Instant calibration brackets were 30 ns minimum, 40 ns median, and 235 ns maximum; they were not subtracted from samples.

All scan slots reported the same work-counter deltas: one run-file open, one metadata call, 36 header bytes, 24 footer bytes, 96 directory bytes, 60,436 run-block bytes, zero cache evictions/rereads, zero mutable keys examined, one next-key descriptor and 2,048 next-key records examined, and zero point lookups/descriptors/records. The full per-scan counter objects, phase values, checksums, and errors remain in samples.jsonl.

Runtime identity: benchmark observation checkout HEAD 5685f163bb7e9339e8eb0c839fe2512aaf562648; TerraBase observation checkout HEAD 352d9f61185d1b685e3032519579040d169ad47d. Both were clean. The pinned controller SHA-256 was 32266d9ec86404070d71f1b12ac43150208cb004634dde12afc6a3dd28be8f20; guest agent 3a2b582bd911e654aaa1a7181e0761dee6231cc317270bceca62486b7ea0c6bb; Firecracker 1.15.1. The guest used four vCPUs, 8,192 MiB, swap disabled, and no network. Kernel SHA-256 was e20e46d0c36c55c0d1014eb20576171b3f3d922260d9f792017aeff53af3d4f2; rootfs SHA-256 was dd49aee1d9c28f8e781a627bec744db3a961ce4402950130ff9268504c551314 before and after the run. Upstream kernel/rootfs provenance is unqualified because the pinned local historical manifest lacks upstream artifact URLs. The host was an Intel Xeon E5-2660 v4; the guest was pinned to host CPUs 0–3. Host swap was active and a pre-existing dashboard make dev process was left untouched. Cache contention was not directly measured; timings include scheduling and instrumentation effects.

The separate supporting baseline test guest passed first: run p04d02supportunit20260924t2258z, payload SHA-256 752d0d9de16ea77b078136c10ae8ad25f2743030d025d4b1e3818326330cbae3, six tests passed and zero failed. It was built from the separate 82-file support-test packet; it is not the D02 program or source identity.

The generated guest report is preserved unchanged as report.md. The receipt, all 33 ordered JSONL samples, and guest manifest are retained beside it. Raw harness evidence (run manifest, Firecracker logs, and guest-output archive) remains under the corresponding bench/firecracker-harness/p04d02a2diag20260924t2258z/ run directory.
