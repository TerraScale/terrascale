# SE-15 pinned PostgreSQL reference identity record

Status: the **decided** pin for the PostgreSQL reference baseline of
charter PD-2026-08-26-03 D2, recorded for ticket TerraScale/TerraBaseV2#23
the way the V1 source pin is recorded. The machine-checked summary of this
document is `src/benchmarks/reference.rs` (`POSTGRESQL_REFERENCE`), whose
tests pin the decision and the deliberately empty observation slot.

## What is decided, and what is only observed

This record separates two things that are easy to blur:

- **Decided** (below, and in `POSTGRESQL_REFERENCE`): the major series, the
  configuration posture, the schema, and the driver shape. A decision can be
  made without a server, and it was.
- **Observed** (empty until a lane run): the exact point release, the
  server's own `SELECT version()` output, the artifact digest, and the
  verbatim values of every default this record leaves to the artifact.

**No `version()` string appears anywhere in this repository, because none
has ever been observed.** The observation slot is `None` and the adapter's
tests pin that it is `None`. It is filled only by capturing the server's own
output at a lane run, and the pin is not complete until then.

## The decision

**PostgreSQL 18**, the newest fully-supported stable major series
(the PostgreSQL versioning policy, https://www.postgresql.org/support/versioning/),
run at that series' then-current minor release as shipped by the pinned
artifact. PostgreSQL 17 is the recorded fallback comparand if the operator's
environment pins 17; the decision records whichever series the pinned
artifact actually belongs to, verbatim from `SELECT version()`.

Rationale, stated so it can be disagreed with: the objective is
"as fast as PostgreSQL", and a reference older than the current stable
series would quietly flatter TerraBaseV2 against a comparand nobody runs.
Choosing the newest fully-supported series is the conservative reading.

## Server configuration (the pin)

Every non-default setting the reference runs with. Anything not listed here
is at the pinned artifact's shipped default, and its verbatim value is
captured at the run.

| Setting | Value |
| --- | --- |
| `server_version` | PostgreSQL 18, the newest fully-supported stable major series; the minor release is whatever the pinned artifact ships, recorded verbatim from `SELECT version()` at the run |
| `synchronous_commit` | `on` — the acknowledgement level that matches the engine's fsync-per-commit commit path |
| `fsync` | `on` |
| `full_page_writes` | `on` |
| `data_directory` | fresh empty directory per run, on the same storage class as the TerraBaseV2 engine location, discarded afterwards |
| `logging_collector` | `off` — no query logging during measured samples |
| planner | every planner default; no query hints, no auto-explain, no per-run `ANALYZE` beyond what warm-up naturally produces |
| `shared_buffers` / `work_mem` | the pinned artifact's shipped defaults, recorded verbatim at the run |
| schema | one table `users(tenant text, id text, name text, "group" int, payload text, PRIMARY KEY (tenant, id))` with a btree index on `(tenant, name)`, created before warm-up |
| listen / authentication | loopback-only socket, trust authentication for the benchmark role only |
| resource limits | none — no container CPU or memory cap, matching the TerraBaseV2 side |

The three durability settings are pinned **on** deliberately: the TerraBaseV2
side commits at `LocalDurability::Synced`, so weakening the reference's
durability would be the single easiest place for a hidden tuning asymmetry
to hide. Nothing on either side is weakened for timing.

## Client configuration (the pin)

| Setting | Value |
| --- | --- |
| driver | one connection per concurrency slot, prepared statements, no application-level cache |
| concurrency | 16 in-flight operations, matching the TerraBaseV2 side |
| warm-up | 1,000 operations per workload before any measured sample, excluded from every measured sample |
| order | PostgreSQL and TerraBaseV2 alternate which side runs first each sample |
| isolation | each statement runs in its own transaction (autocommit), matching the engine's per-operation acknowledgement |

## How the reference side is reached

The engine workspace adds **no** PostgreSQL client dependency, so the
reference side is driven by the operator at the lane run, against an
endpoint named by the environment variable `TERRABASE_PG_REFERENCE_ENDPOINT`
(only the variable's *name* is recorded in source; no endpoint value,
connection string, or credential belongs in this repository). The adapter's
`postgresql_reference_run()` reports the honest status:

- No endpoint configured and no local PostgreSQL → `Unrun`, reason naming
  both facts, owner named below.
- Endpoint configured but this adapter build carries no client → still
  `Unrun`, for the reason stated; configuring a variable does not create a
  driver.

## Owner of closing the gap

**TerraScale benchmark lane owner (SE-15)** owns providing the pinned
PostgreSQL 18 endpoint, capturing its `SELECT version()` output and artifact
digest, filling the observation slot in
`src/benchmarks/reference.rs`, and running the reference side of the
baseline. Until that happens, every receipt in `docs/evidence/benchmarks/`
records the PostgreSQL reference as unrun-with-reason, and no comparison
verdict or objective status other than `inconclusive` is reachable — the
adapter's fail-closed rule returns exactly that while either reference is
`Unrun`.

## The TerraBase V1 reference pin, for contrast

The V1 baseline is pinned the way this repository already pins it:
source commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`, harness
`bench/TerraBase.Server.Benchmark/ServerComparisonBenchmark.cs`, built
Release, fresh tmpfs data per run, loopback-only, no resource limits. Its
observed identity *is* filled, because a source commit is observable
without running it — which is exactly the distinction this document exists
to keep honest: a commit is an identity you can read, a `version()` output
is an identity only a running server can give you.
