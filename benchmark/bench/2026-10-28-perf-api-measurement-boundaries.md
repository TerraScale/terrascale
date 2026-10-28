# API benchmark measurement boundaries: source audit, 2026-10-28

Read-only source audit at main `a42f65d558384ed2728f2799c70fe98addf6924f`.
The three files below are unchanged from `a5999f5ea`, the CORS comparison base.
No executable, measurement window, production code or benchmark result changed.
Nothing was built or run for this audit.

This follows the slow first small/c1 cell seen in the independent CORS repeat
([report at its retained commit](https://github.com/TerraScale/terrascale/blob/9cbb2126b9ea48f70b24bcac252bfb0a22bc684c/benchmark/bench/2026-10-28-perf-api-cors-span-tail-recheck.md)). That report retains every
completed arm and its unresolved verdict. This audit does not identify the
cause of the slow state or settle whether an optimization changes its incidence.

## Source boundaries

| Source at the pinned commit | What the code establishes |
|---|---|
| `benchmark/dotnet/VsTurso/VsTursoBenchmark.cs`, `MeasureAsync` | Each cell uses a fixed ten-second warmup followed by the configured window. There is no steady-state detection gate. |
| Same method, CPU/allocation snapshots | The first worker reaching the measurement boundary snapshots process CPU then total managed allocated bytes. End snapshots occur after all workers have returned. These are process-wide client, server and background costs. |
| Same method, operation count | A successful operation counts only when it begins and finishes inside the window. Work in an operation still completing after the window can enter the CPU/allocation numerator without entering that operation count. The effect is not quantified here. |
| Same method, marker publication | `cpuMarked` is exchanged before the CPU and allocation snapshots are assigned. Other workers can start work while those snapshots are being taken; their boundaries are not one atomic snapshot. No measured error bound is available. |
| `benchmark/dotnet/VsTurso/ServerAttribution.cs` | A server sample joins a hosting Activity started in the window and records only when it stops by the end. Its engine timing is paired with that request's hosting time. These quantiles have a different boundary from process-wide CPU/allocation snapshots. |
| Same file, `Capture` | The reservoir retains at most 16,384 paired server/engine timings per cell. Server, engine and per-request difference quantiles are each sorted independently. Overhead p50 is not server p50 minus engine p50. |
| `benchmark/dotnet/VsTurso/ApiTarget.cs`, `GetAsync` | The loopback API target reads the whole response body and rejects non-success HTTP statuses. It does not validate the returned document or require exactly HTTP 200. Zero errors here is not exact-document parity evidence. |

## Interpretation

- Fixed warmup does not establish that JIT, caches or background work have reached
  a stable state. Their involvement in the reported first-cell state is a
  hypothesis, not a finding from this read.
- Process B/op and CPU/op are not server-only quantities. The numerator and
  denominator boundaries above can differ near the window edges. Their effect
  on the prior results has not been measured; this audit supplies no correction.
- The observed first-cell allocation difference was about 5.5 KB/op. None of the
  source observations above establishes an explanation for that magnitude.
- Preserve the original tables, all arms and declared assessment rules. A source
  hypothesis is not grounds to drop an arm, select favorable cells, lengthen
  warmup after seeing results, or call overlapping ranges equivalent.
- The authenticated regional runner's exact-body, status, protocol, attempt and
  region checks are separate qualification. A local API cell cannot substitute
  for those checks or for live gru/sjc/fra evidence.
- A future harness correction or startup diagnostic needs its own qualification
  and a new result series. Do not alter frozen before/after binaries mid-run.

The engine denominator and original server-over-engine target are unchanged.
This audit makes no speedup claim, does not reconcile the earlier owned database,
and does not authorize a new tenant attempt.
