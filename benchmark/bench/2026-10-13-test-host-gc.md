# Test-host CPU and GC sizing, 2026-10-13

| Observation | CPU / GC heaps | Wall time | Cumulative GC pause | Result |
|---|---:|---:|---:|---|
| Before: [#207 TerraBase job](https://github.com/TerraScale/terrascale/actions/runs/38447780403/job/115470667881), shared self-hosted `alpha-s4-1791915955` | 44 / 44 | 119.076 s sample | 88.947 s (74.697%) | Load 47.07; thread-pool starvation and two-minute failures |
| Same before job, final runtime sample | 44 / 44 | 708.645 s | 403.741 s (56.974%) | Ten Fast failures; six two-minute timeouts |
| After: this PR's Blacksmith TerraBase job | Pending | Pending | Pending | CI will supply the runner's measured CPU count to the Docker test ancestry |
| Local before, same source / mariogk-dev | 46 / 46 | 237.559 s suite; 235.359 s final sample | 41.258 s (17.530% of final sample) | Fast1561/1561 and native6/6 passed; at119.017s pause23.939s, load24.97 |
| Local after, same source / mariogk-dev | 8 / 8 | 200.331 s suite; 198.440 s final sample | 15.972 s (8.049% of final sample) | Fast1561/1561 and native6/6 passed; at119.027s pause12.246s, load18.12 |
| Production Fly | Configured shared 2 CPUs in all gru/sjc/fra TerraBase/auth machines | — | — | Machines stopped in T2's rebuild; runtime CPU count not yet verified |

[Bounded baseline samples](2026-10-13-test-host-gc-baseline.json) were extracted directly from the official job log using gh. BuildKit could not read cgroup quotas in that run. Alpha's 44 visible CPUs are its shared host, not evidence of a two-CPU quota.

CI now selects fixed Blacksmith runners: 2 CPUs for bookkeeping, 4 for libs/auth/preview/e2e, 8 for TerraBase/Slow. Each Docker test build receives the runner's nproc via DOTNET_PROCESSOR_COUNT so nested test hosts inherit that allocation before the CLR starts. The independent publish/runtime Docker stages retain native Fly CPU discovery. The deploy job is byte-identical to main; its coordinated runner change remains deferred to T2. Existing Fast/Slow assertions, test counts, timeouts and parallelism are unchanged.

Hosted before/after runs have different source and runner inputs; their timings describe the resulting CI behavior, not an isolated GC causal estimate. The local comparison uses identical source and host, changing only the runtime processor count. Local observed Fast duration fell15.7% and cumulativeGCpause61.3%; host load also declined, so this is not isolated causal proof. Hosted after results remain pending. CI attempt1 was cancelled before any runner claimed the changes job; every cancelled job had zero steps. Attempt2 was restarted on the same head at19:19:52Z.
