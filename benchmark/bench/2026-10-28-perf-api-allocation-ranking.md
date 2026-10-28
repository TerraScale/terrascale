# Keyed API allocation attribution, 2026-10-28

Investigation evidence only: no new speedup, CPU attribution, quiet comparison or
live measurement. On delta, reuse the after binary from the key-watch report:
`terrabase.dll` SHA-256 `ec66f5d759c31331babb3d41d72854ccac65bf2f76f585751954a53c5855d6d5`.
The recorded harness/SDK sources and limits remain those of that report.

Ran through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 bash
/tmp/perf-api-allocation-profile.sh --repo terrascale --branch fix/api-keywatch-timer`.
The benchmark used api/read, c=16, 20 s cells, one repeat, with its ten-second
warmup. The collector attached twelve seconds after the flushed preload marker
and recorded ten seconds of `gc-verbose` during the small-read cell. No rebuild
or production call occurred. Both benchmark cells passed; the traced cell is
perturbed and supplies no latency comparison.

Microsoft dotnet-trace 10.0.750501 produced a 36 MiB EventPipe trace. TraceEvent
3.2.5 read 104,692 allocation ticks with 11,162,841,368 interval-weight bytes;
all samples had stacks. Closest frames matching the selected server namespaces:

| Closest selected frame | Allocation tick interval weight, bytes |
| --- | ---: |
| `UserAuthenticationHandler.HandleAuthenticateAsync` | 837,590,936 |
| `UserAuthenticationHandler.Success` | 789,961,816 |
| ASP.NET `HostingApplicationDiagnostics.StartActivity` | 622,117,888 |
| `EngineManagementService.PlanAsync` | 443,325,456 |
| `QosExtensions.UseTerraBaseQosMemory` callback | 371,930,448 |
| `GetDocumentEndpoint.Conditional` | 300,153,784 |
| `UsageRequestMetering` callback | 180,139,776 |

These are sampled interval weights attributed to the closest selected stack
frame, not exact bytes allocated by that method. Sampling bias, inlining, child
calls, client/background work and the benchmark's Activity listener remain.
The data prioritizes authentication materialization and catalog/admission for
investigation; it does not justify removing claims isolation, memory admission,
revocation, metering or instrumentation. Source inspection found transient UTF-8
and digest arrays in token hashing; their removal is a candidate, not a measured win.

Raw trace, scripts, hashes, cells and compiled analysis source/build logs are
retained on delta in managed `fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e`,
`benchmark/bench/api-allocation-20261028-a/` (`analysis-c/` is the final attribution).
The first analysis build failed style checks; the corrected build passed with
zero warnings/errors. No guard was suppressed.
