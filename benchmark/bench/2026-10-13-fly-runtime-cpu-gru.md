# Fly production runtime CPU qualification, 2026-10-13

| Observation | Result | Evidence |
|---|---|---|
| App / machine / region / revision | ts-terrabase /1857503f017528 /gru /a537727b | T2 handoff and machine status |
| Configured guest CPUs | 2 shared CPUs,512MB | flyctl machine status display-config |
| Actual .NET ProcessorCount | **2** | System.Runtime metric: `dotnet_process_cpu_count{otel_scope_name="System.Runtime"} 2` |
| Test-only CPU/GC environment overrides | None | Allowlisted machine environment inspection |
| Published-source GC mode | Workstation GC configured | a537727b TerraBase.csproj ServerGarbageCollection=false |
| Actual live HeapCount | Not exposed by existing metrics | No heap-count instrument; single-file runtime config not available as a separate file |

Read-only inspection used a Fly tunnel to this exact machine's private9091metrics listener. The tunnel was closed afterward. No machine restart, process restart, environment update, deploy, file upload or resource allocation was performed. Source configuration does not constitute a direct live HeapCount measurement. Full regional rollout remains pending under T2. [Allowlisted observations](2026-10-13-fly-runtime-cpu-gru.json) omit all other machine environment values and service metrics.
