# Keyed-auth server attribution smoke, 2026-10-27

Validation only: one 1-second repeat at c=1 on beta, source `a11f18152`, .NET 11 RC1, workstation GC/PGO.
Built through `ts-heavy-any nice -n 19`; both api/sdk sizes returned nonempty attribution with zero errors.
Project-scoped synthetic key_id/key_gen exercised the key-authority/watch path against TestIssuer; no credentials logged.
This is not a speedup or quiet-host qualification: host load1 was 6.30–7.86, cache/JIT warmup differed between lanes.
Each percentile is independent; overhead is calculated per request. Hosting excludes pre-context Kestrel work.
Reproduce with `run.sh`: lanes=api,sdk, workloads=read, concurrency=1, repeats=1, seconds=1 (TERRABASE_VS_TURSO_*).

| Size | Lane | Client p50/p99 µs | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | Requests | Errors |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| small | api | 517.6/843.6 | 378.5/653.8 | 132.5/217.5 | 241.3/456.1 | 1874 | 0 |
| small | sdk | 405.1/787.2 | 236.8/600.9 | 8.3/189.5 | 223.9/468.6 | 2204 | 0 |
| medium | api | 359.8/682.3 | 284/526.1 | 137.3/235 | 147.5/292.2 | 2808 | 0 |
| medium | sdk | 202.8/788.6 | 107.1/390.1 | 5.5/212.8 | 99.3/199.3 | 3879 | 0 |

The <10% engine-overhead and authenticated live gru/sjc/fra targets remain unproven. Three-repeat attribution follows.
