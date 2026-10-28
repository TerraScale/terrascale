# API allocation-type attribution, 2026-10-28

Diagnostic follow-up to the allocation ranking, using the same frozen trace and
binary. No new requests, latency comparison, CPU attribution or production work.
Interval-weight bytes below are sampled allocation-tick weights, not exact bytes
allocated by a method. JIT inlining, sampling bias and nearest-frame attribution
remain. Types named `Store`/`Enumerator` are event labels, not proven source types.

On delta through `ts-heavy-any nice -n 19`, TraceEvent 3.2.5 re-read the original
36 MiB trace: 104,692 ticks, 11,162,841,368 weighted bytes, no missing stacks.
The analysis joins the closest selected server frame with each tick's TypeName.
It built in Release with zero warnings/errors; shared build servers were disabled.

| Closest selected frame | Type label | Interval weight, bytes |
| --- | --- | ---: |
| PlanAsync | Store | 442,694,008 |
| PlanAsync | PlanTier | 311,688 |
| PlanAsync | DisplayClass146_0 | 213,160 |
| PlanAsync | DisplayClass358_0 | 106,600 |
| Auth Success | Claim | 263,810,136 |
| Auth Success | Claim[] | 218,098,976 |
| Auth Success | Dictionary<string,string> | 187,599,944 |
| Auth Success | tuple Enumerator | 75,894,720 |
| Auth Success | ClaimsIdentity | 26,968,800 |
| Auth HandleAuthenticateAsync | String | 480,903,296 |
| Auth HandleAuthenticateAsync | Byte[] | 353,916,040 |

This contradicts a closure-first explanation of the PlanAsync sample. It supports
checking the semantics of claim materialization, without reusing mutable claims
or principals across requests. It does not prove a change will reduce latency.
The separately measured token-hashing candidate was rejected for mixed latency.

Artifacts on delta: managed `fix-api-token-hash-evidence-72ff07a7a739d3fe9331cba929d7f5d00528b4fe2ed287db776b1e3aba098dd3`,
`benchmark/bench/api-allocation-types-a/`: exact run script, analysis source,
project, lock, build log, complete output, empty error log and SHA-256 manifest.
The trace is in the earlier key-watch managed checkout's
`benchmark/bench/api-allocation-20261028-a/allocation.nettrace`.

| Artifact | SHA-256 |
| --- | --- |
| Analysis Program.cs | `a042a414d02a01200878f6aec3f92ca6005e33654a707cacb63a9dc37c08c9fa` |
| Trace | `acd092fbc0ac0efe1d6740c2e82614c5ef7493b8f8083cb6bd60bb5f8539a8cd` |
