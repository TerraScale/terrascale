# Production runtime CPU qualification in all three regions — 2026-10-13

**Actual .NET ProcessorCount is 2 on all six running machines.** Read-only Fly managed Prometheus query `dotnet_process_cpu_count{app=~"ts-auth|ts-terrabase"}` returned these values. The query follows the [documented Fly endpoint and token authentication](https://fly.io/docs/monitoring/metrics/).

| App | Region | Machine | ProcessorCount | Sample UTC |
|---|---|---|---:|---|
| ts-auth | fra | d8921e1b3e5758 | 2 | 2026-10-13T22:04:21+00:00 |
| ts-auth | gru | 8747930a042038 | 2 | 2026-10-13T22:04:21+00:00 |
| ts-auth | sjc | 850e45b4142318 | 2 | 2026-10-13T22:04:21+00:00 |
| ts-terrabase | fra | 286557e5b45498 | 2 | 2026-10-13T22:04:21+00:00 |
| ts-terrabase | gru | 1857503f017528 | 2 | 2026-10-13T22:04:21+00:00 |
| ts-terrabase | sjc | 847310c2ee95d8 | 2 | 2026-10-13T22:04:21+00:00 |

All six guest configs use two shared CPUs and 512 MB RAM. Allowlisted environment inspection found no test-only .NET CPU/GC override. Both published service projects configure workstation GC (`ServerGarbageCollection=false`); the existing metrics expose no live heap-count measurement. Source configuration is not direct proof of the running heap count.

Auth metrics bind IPv4 and reset a direct 6PN IPv6 connection; managed Prometheus successfully reads them. All local inspection tunnels were closed. No machine/process restart, deploy, environment update, upload, forced GC or memory dump was performed.

[Allowlisted metric samples](2026-10-13-fly-runtime-cpu-all-regions.json) contain only app/region/machine labels, sample times and CPU values.
