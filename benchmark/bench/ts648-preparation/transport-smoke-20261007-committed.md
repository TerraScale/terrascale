# TS-648 actor transport diagnostic

This measures the preparatory logical-actor transport, not database throughput or durability. No core affinity is applied.

UTC: 2026-10-07T20:55:23.7092101+00:00; available processors: 12; runtime: 11.0.0.
TerraBase revision: 70c6e9e89ff89439278440b25c825981e8c04e6f (include working diff when dirty).
Harness revision: 9abc6cd633adfe8824ccd2783fdbd24a18e0dfcb (include working diff and untracked sources when dirty).
Operations/trial: 128; CPU iterations/operation: 20; keys: 64; producers: 32; payload admission: 64 bytes.

| Actors | Zipf exponent | Repeat | ops/s | p99 ms | CPU seconds | Peak RSS MiB | Callback count |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | 1 | 29724.6 | 0.073 | 0.004 | 48.2 | 128 |
| 2 | 0 | 1 | 140752.1 | 0.367 | 0.001 | 49.6 | 128 |
| 1 | 0.99 | 1 | 151712.7 | 0.356 | 0.001 | 49.8 | 128 |
| 2 | 0.99 | 1 | 70988.9 | 0.024 | 0.002 | 50.0 | 128 |
| 1 | 1.2 | 1 | 121166.2 | 0.337 | 0.001 | 50.2 | 128 |
| 2 | 1.2 | 1 | 138050.0 | 0.409 | 0.001 | 50.4 | 128 |

CPU and process-lifetime peak RSS include the load generator; they are not separate server measurements. Repeats run in one process.
A Zipf hot key remains serialized on its owner. Memory replication and the SQLite flusher are not measured.

Smoke receipt only: the shared host was concurrently building/testing other worktrees. The 128-operation trials verify harness execution and callback counts; their timings are not scaling evidence or a performance baseline.
