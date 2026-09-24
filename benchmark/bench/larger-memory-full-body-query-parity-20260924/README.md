# Larger-than-memory full-body physical-query parity

This is a separate workload from `../larger-memory-physical-query-parity-20260924/`. The earlier SQLite queries selected only IDs while TerraBase `DurableIndexStore.QueryAsync` decoded complete bodies. Its receipts remain unchanged and are evidence for capacity and their recorded API boundaries, not a full-body speed comparison. The earlier runner also inserted SQLite's 100 maintenance seed rows in one transaction while TerraBase used four 25-row frames; its build and maintenance times do not have equal transaction sizing.

This runner references the frozen TerraBase and ZoneTree assemblies from the successful continuous run `../larger-memory-physical-query-parity-20260924/run-20260924T054213Z/`, copied into `frozen-runtime/`. It does not build against the moving live TerraBase project. On startup it checks both loaded assembly hashes against that run's `run-start.json`, and its source provenance points to that run's frozen source copies. The new runner source, SQL shape and SQLite transaction plan have their own hashes in every new receipt.

The new runner uses the same 3,000 base plus 100 maintenance documents, 98,304 raw payload bytes per document, two fresh databases per engine, and one physical equality index per database. SQLite retains a JSONB `body` and adds an `INTEGER NOT NULL` version column. It seeds and updates in 25-row transactions, as TerraBase does. The runner asserts and records 120 base, four maintenance insert, and four maintenance update transactions per lane, with a maximum of 25 documents per transaction.

SQLite times this exact SQL for `$.status` and `$.profile.segment`, substituting the literal path and parameters:

```sql
SELECT id, json(body), version
FROM users
WHERE json_extract(body, '$.status')=@value
ORDER BY id LIMIT @limit;
```

`EXPLAIN QUERY PLAN` uses the same select list and predicate. During the timed query, the runner reads each ID and version, converts each JSONB body with `json(body)`, parses the returned JSON text as `JsonDocument`, clones the root, and retains all 31 materialized bodies. TerraBase's timed `QueryAsync` decodes and returns complete `Document` bodies; the runner extracts IDs and retains those documents. After timing, both sides verify exact ordered IDs, `status`, nested `profile.segment`, `value`, the full deterministic Base64 payload, and version 1 or 2 for every returned body on every sample. ID output serialization is timed separately. The raw receipt records materialized body and payload-character counts.

The two engines still have different physical formats and materialization paths: SQLite performs JSONB-to-text conversion and JSON parsing; TerraBase decodes its stored JSON and clones the document body. The direct index store omits TerraBase's authoritative source store, planner, advisor, API, transport, and replication. The result is an API-boundary physical-query comparison, not pure engine cycles or a cross-engine superiority claim.

Build and run from this directory. The qualified command must run alone in a 256 MiB zero-swap cgroup with a 128 MiB GC heap cap:

```sh
dotnet build FullBodyPhysicalQuery.csproj -c Release
systemd-run --user --scope --collect --quiet \
  -p MemoryMax=256M -p MemorySwapMax=0 -p OOMPolicy=stop \
  env DOTNET_GCHeapHardLimit=8000000 \
  dotnet bin/Release/net11.0/FullBodyPhysicalQuery.dll --continuous
```

For an uncapped, unqualified functionality check, use `dotnet run --project FullBodyPhysicalQuery.csproj -c Release --no-build -- --smoke --continuous`. The isolated Release gate completed before the full-body run was launched.

## Completed run

The source-matched smoke is in `run-20260924T062609Z/`. The qualified full-body run is in `run-20260924T062637Z/`, with `physical-query.md`, `physical-query-raw.json`, `run-start.json`, and hash-checked `frozen-provenance.json` plus source/assembly copies. The qualified run exited zero under a 256 MiB memory cap, zero swap and a 128 MiB GC heap cap. The final cgroup sample records `memory.peak=268,435,456`, `memory.events max=98667`, `oom=0`, and `oom_kill=0`.

All four lanes recorded 120 base, four maintenance insert and four maintenance update transactions of at most 25 documents. The two SQLite physical files were 408,162,304 bytes each. The TerraBase physical files were 306,995,087 and 306,994,851 bytes. Every first and repeat query verified the exact ordered IDs, bodies, full deterministic payloads and versions; each first query materialized 31 bodies with 4,063,232 Base64 payload characters. Both TerraBase lanes reopened at watermark 128 and separately verified all 3,100 committed documents. The exact first and repeat timings, index/scan plans, cgroup counters and process I/O are in the raw receipt. This one run supports the stated full-body API-boundary comparison under this fixture and does not establish a cross-engine speed superiority claim.
