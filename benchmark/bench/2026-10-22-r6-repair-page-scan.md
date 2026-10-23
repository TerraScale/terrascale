# R6 full-bucket key-page range seek — 2026-10-22

Query-only diagnostic on beta using pinned native Turso 0.8.2. A uses the original full-bucket predicate; B retains it and adds its implied `bucket >= $bucket` bound. The production change is `666406fa4`, local-only pending FR1. No protocol, schema or durability behavior changes.

Three paired repetitions per size, back-to-back on the same host, alternating AB, BA, AB. Each worker opens an identical fresh synthetic file copy and measures three page requests: initial, middle bucket 2048, final bucket 4095. Each request executes the production key preflight and projection at LIMIT 1025. Seeding, opening, expected-result calculation and digest assertions are outside timing. A page includes one lookahead key.

| Rows | Metric | A median [min, max] | B median [min, max] | Median change |
| ---: | --- | ---: | ---: | ---: |
| 100,000 | Initial page ms | 285.302 [269.569, 322.634] | 29.569 [19.948, 31.871] | -89.64% |
| 100,000 | Middle page ms | 69.522 [69.370, 72.558] | 28.953 [17.878, 31.327] | -58.35% |
| 100,000 | Final page ms | 0.880 [0.863, 0.914] | 1.872 [1.409, 2.065] | +112.68% |
| 100,000 | Three requests s | 0.356 [0.343, 0.393] | 0.062 [0.040, 0.063] | -82.48% |
| 100,000 | Absolute process peak RSS MiB | 59.734 [59.527, 60.980] | 31.773 [31.672, 31.953] | -46.81% |
| 1,000,000 | Initial page ms | 5708.311 [5111.548, 6297.388] | 1173.408 [215.136, 1356.291] | -79.44% |
| 1,000,000 | Middle page ms | 832.993 [784.748, 1646.720] | 1163.092 [208.581, 1356.451] | +39.63% |
| 1,000,000 | Final page ms | 6.132 [6.096, 10.553] | 9.509 [8.236, 10.978] | +55.06% |
| 1,000,000 | Three requests s | 6.547 [5.902, 7.955] | 2.346 [0.432, 2.724] | -64.17% |
| 1,000,000 | Absolute process peak RSS MiB | 78.883 [78.367, 79.500] | 32.160 [31.965, 33.184] | -59.23% |

All 36 measured requests matched the independently generated ordered-key count/SHA256 oracle. Before timing, both complete walks of a separate 4,103-key dataset matched exact count/digest and strict ordering; six checkpoint checks include same-bucket continuation, transitions, final bucket and BMP/supplementary Unicode keys. These checks prove the sampled results and small walk, not full large-dataset coverage.

Native EXPLAIN shows why the bound matters: A uses MULTI-INDEX OR and sorts the remaining suffix before LIMIT for both SQL queries; B uses `_tb_state_bucket` range seeks without a sorter. This removes repeated suffix sorting. A hot individual bucket can still require skipping earlier keys. Subset-bucket queries are unchanged.

The synthetic rows use uniform arithmetic buckets, short fixed-width IDs and minimal state payload; they do not reproduce production key hashing or document size. Timing includes native execution, ctypes key decoding and page checks, and excludes document decoding, gRPC, receiver FULL repairs, salvage and certification. These numbers are key-page latency, not traversal throughput, rebuild time or catch-up time.

Peak RSS is absolute Linux child ru_maxrss including interpreter, oracle, native cache, sorter and page objects. Seeding runs in a separate process. It is neither managed-heap allocation nor a query-only memory delta. Requests share the worker cache in the stated order; OS page cache and other host load are uncontrolled. The spread is reported rather than treated as a significance estimate.

Command: `nice -n 19 python3 benchmark/rebuild-page-scan.py --run --output <new-jsonl-path>`, admitted through `ts-heavy-any` on beta, managed key `feat/repl-benchmark-r0`. The script runs the small selfcheck before seeding large datasets. No TerraBase binary is launched by this diagnostic. The separate certified end-to-end 1M matrix remains untouched.

| Reproducibility item | SHA256 |
| --- | --- |
| Script | `e5d09ca1ac176e08e805def5b62cf7a71173a00b8e1797a621814a5eb2d253e4` |
| Native Turso 0.8.2 library | `56378e447d2efef78483d14eaf1e1972e567c44abb7b5238f592939903ed7fb4` |
| Raw 12-sample JSONL | `ad9c46e2c562b33ff43d6f7e88654494be651341d0b4d400efa99c0fa72dbe67` |

Raw samples: `2026-10-22-r6-repair-page-samples.jsonl`. They include predicates, schema/query hashes, platform, repetition/order and each cursor digest/count. Full rebuild and catch-up results belong to `2026-10-22-r6-rebuild.md`; this diagnostic supplies no replacement for those measurements.
