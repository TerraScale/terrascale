# TS-648 actor transport diagnostic

This measures the preparatory logical-actor transport, not database throughput or durability. No core affinity is applied.

UTC: 2026-10-07T23:02:02.6680162+00:00; available processors: 2; runtime: 11.0.0.
TerraBase revision: 1dc03cc458c0c37a53edbb1e4a9d790e17dc684d (include working diff when dirty).
Harness revision: 80e0836b8d93c373967064e66b459f6480586306 (include working diff and untracked sources when dirty).
Operations/trial: 128; CPU iterations/operation: 20; keys: 64; producers: 32; payload admission: 64 bytes.

| Actors | Zipf exponent | Repeat | ops/s | p99 ms | CPU seconds | Peak RSS MiB | Callback count |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | 1 | 58248.0 | 0.217 | 0.004 | 47.4 | 128 |
| 2 | 0 | 1 | 96977.0 | 0.038 | 0.001 | 50.4 | 128 |
| 1 | 0.99 | 1 | 140197.2 | 0.431 | 0.001 | 50.6 | 128 |
| 2 | 0.99 | 1 | 168887.7 | 0.326 | 0.001 | 50.9 | 128 |
| 1 | 1.2 | 1 | 143369.2 | 0.365 | 0.001 | 51.2 | 128 |
| 2 | 1.2 | 1 | 173019.7 | 0.537 | 0.001 | 51.3 | 128 |

CPU and process-lifetime peak RSS include the load generator; they are not separate server measurements. Repeats run in one process.
A Zipf hot key remains serialized on its owner. Memory replication and the SQLite flusher are not measured.

Smoke receipt only: this run used two available processors and a two-core CPU quota on a shared host, at low CPU priority. The 128-operation trials verify harness execution and callback counts; their timings are not scaling evidence or a performance baseline. Both recorded source revisions were committed before execution.
