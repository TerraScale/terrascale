# SQLite physical-index parity baseline

This is an independent SQLite replay of the 2,000-document held-out collection workload in `terrabase/TerraBase.Benchmarks/QueryIndependentIndexWorkload.cs`. The executable C# source is the preserved benchmark script. It uses `Microsoft.Data.Sqlite` 10.0.12, native SQLite JSONB, a primary key, and zero or two expression indexes. It writes three fresh trials, each with separate scan and indexed databases.

Run from this directory:

```sh
dotnet run --project SqlitePhysicalIndexParity.csproj -c Release
```

Each run writes a new `run-YYYYMMDDTHHMMSSZ/` directory with a Markdown table and raw JSON. The raw receipt records expected and returned ID lists, every repeat sample, EXPLAIN plans, observed PRAGMAs, source SHA-256 hashes, host CPU and memory, SDK/runtime and SQLite versions, and Git state. The program fails on any ID or selected-index mismatch. The run keeps the C# seed generator's exact 2,000 base documents and 100 maintenance documents: `doc-000000` through `doc-001999`, `doc-maint-000000` through `doc-maint-000099`, 100-row transactions, then one 100-row maintenance update after indexes are built. The `status=s17` and `profile.segment=g23` queries each return 21 ordered IDs from this data. The 100-row limit is above both result counts. The maintenance update changes only `value`, so it leaves the held-out membership unchanged while exercising index maintenance.

The measured query boundary is prepared SQLite command execution and ID reading. EXPLAIN and Prepare run before the first timed call. Advisor candidate generation, Von/Jev inference, host API, transport, and replication are outside these timings. The two expression indexes are deliberately chosen with held-out predicate knowledge. This is a tuned physical-index oracle, not a strategy comparison with the query-independent advisor.

WAL and `synchronous=FULL` are asserted after connection setup. The SQLite cache is set to 64 MiB and mmap to 64 MiB, matching the held-out SQLite lane. These are soft settings. The TerraBase held-out arm has an 8 MiB physical-index admission cap, while SQLite has no equivalent cap here; file sizes are reported to disclose consumption. The process is not pinned or given a hard cgroup quota. The page cache is not evicted between trials. Reports therefore do not claim cold-disk latency or a fair cross-engine speed ratio.

For a strict future two-engine comparison, run the same dataset and assert the full ordered ID arrays through both physical query APIs in one paired process. Fix the same CPU affinity and cgroup memory/IO envelope, include planner and materialization consistently, warm or evict caches identically, and interleave trial order. Record both engines' index names/fields, index build and post-build catch-up, WAL durability and transaction sizes, as well as peak memory, disk blocks, I/O, and write amplification. Treat TerraBase's admission budget and SQLite's measured resource use separately unless both have a common enforceable budget contract. Measure advisor inference in a separate end-to-end strategy lane with the same visible-index and resource budgets.

The historical held-out receipt used to check the source workload is `../query-independent-heldout-20260924T0358Z-trial3/query-independent-indexing-receipt.json`; its hash is in the raw receipt. The original held-out receipts report counts and correctness, not full result ID arrays, so this replay cannot retroactively prove byte-for-byte result parity with that prior TerraBase run. It does provide deterministic expected IDs and independently verifies every SQLite execution against them.
