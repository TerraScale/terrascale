# Test-host CPU and GC sizing, 2026-10-13

| Observation | CPU / GC heaps | Wall time | Cumulative GC pause | Result |
|---|---:|---:|---:|---|
| Before: [#207 TerraBase job](https://github.com/TerraScale/terrascale/actions/runs/38447780403/job/115470667881), shared self-hosted `alpha-s4-1791915955` | 44 / 44 | 119.076 s sample | 88.947 s (74.697%) | Load 47.07; thread-pool starvation and two-minute failures |
| Same before job, final runtime sample | 44 / 44 | 708.645 s | 403.741 s (56.974%) | Ten Fast failures; six two-minute timeouts |
| After: this PR's CPU-limited alpha TerraBase job | Pending | Pending | Pending | CI will supply the runner's measured CPU count to the Docker test ancestry |
| Alpha disposable runner qualification, 19:37:50Z | Outer and inner Docker quotas both 8 CPUs | 9.1 s BuildKit bootstrap | Not measured: no tests in this infrastructure check | BuildKit running; Docker inspect and inner cpu.max both verify 800000 / 100000 |
| Local before, same source / mariogk-dev | 46 / 46 | 237.559 s suite; 235.359 s final sample | 41.258 s (17.530% of final sample) | Fast1561/1561 and native6/6 passed; at119.017s pause23.939s, load24.97 |
| Local after, same source / mariogk-dev | 8 / 8 | 200.331 s suite; 198.440 s final sample | 15.972 s (8.049% of final sample) | Fast1561/1561 and native6/6 passed; at119.027s pause12.246s, load18.12 |
| Production Fly | Configured shared 2 CPUs in all gru/sjc/fra TerraBase/auth machines | — | — | Machines stopped in T2's rebuild; runtime CPU count not yet verified |

[Bounded baseline samples](2026-10-13-test-host-gc-baseline.json) were extracted directly from the official job log using gh. BuildKit could not read cgroup quotas in that run. Alpha's 44 visible CPUs are its shared host, not evidence of a two-CPU quota.

COS changed the runner decision after confirming Blacksmith is not installed for the org. CI retains alpha. Live read-only inspection of `/etc/ci-runner/broker.json` on alpha found five slots. With44 visibleCPUs, each slot gets floor(44/5)=8 CPUs, leaving4 to the shared host. Each job uses its own Docker BuildKit container with CPUQuota800000/CPUPeriod100000 (equivalent to docker --cpus=8), verifies that limit, and passes8 through DOTNET_PROCESSOR_COUNT before the CLR starts. The independent publish/runtime Docker stages retain native Fly CPU discovery. The deploy job is byte-identical to main; its coordinated runner change remains deferred to T2. Existing Fast/Slow assertions, test counts, timeouts and parallelism are unchanged.

Hosted before/after runs have different source and runner inputs; their timings describe the resulting CI behavior, not an isolated GC causal estimate. The local comparison uses identical source and host, changing only the runtime processor count. Local observed Fast duration fell15.7% and cumulativeGCpause61.3%; host load also declined, so this is not isolated causal proof. Hosted after results remain pending. CI attempt1 was cancelled before any runner claimed the changes job; every cancelled job had zero steps. Attempt2 was restarted on the same head at19:19:52Z.

PR CI attempts1/2 targeted Blacksmith but no runner claimed any job. COS cancelled attempt1 by mistake and restarted it; this is not a failed test result. The next push retains alpha with sizing/isolation only, following the explicit COS decision.

The first alpha run, [38454836967](https://github.com/TerraScale/terrascale/actions/runs/38454836967), failed every area during BuildKit startup before any build or test. Nested Docker rejected its cgroup as invalid: the ephemeral job's root cgroup contained processes and had no delegated CPU controller. Moving those job processes into a leaf and delegating CPU at the job root allows nested Docker quotas; the disposable qualification above exercised the same image and Buildx settings. Its container and anonymous Docker volume were removed automatically. The workflow performs this setup only inside its own ephemeral runner container, preserving the host and other jobs. Hosted tests and GC measurements remain pending on the corrected head.
