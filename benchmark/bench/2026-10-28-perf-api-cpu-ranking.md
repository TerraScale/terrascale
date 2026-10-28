# Owned keyed API CPU samples, 2026-10-28

Diagnostic only: one ten-second kernel CPU capture, not a quiet before/after
comparison, production measurement or speedup. Reuses the frozen key-watch after
binary from [the measured comparison](2026-10-28-perf-api-keywatch.md),
`terrabase.dll` SHA-256
`ec66f5d759c31331babb3d41d72854ccac65bf2f76f585751954a53c5855d6d5`.
No rebuild of the benchmark or server and no production requests occurred.

On delta through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19`, the keyed api/read
harness ran c=16, one repeat, 20-second cells. The collector attached twelve
seconds after the preload marker, after the harness's ten-second warmup, during
the small-read cell. Both small/medium cells completed with zero errors. Their
timings are perturbed diagnostic output and establish no latency result.

`dotnet-trace` 10.0.750501 `collect-linux -p <owned-pid> --profile cpu-sampling`
captured ten seconds with a 256 MiB collector limit. The owned-process probe
succeeded. TraceEvent 3.2.5 found 87,022 `Universal.Events/cpu` events, all with
stacks and all attributed to the same owned benchmark PID; no other process
contributed CPU events. This covers client, server and background work together.

The built-in topN report attributes every sample's exclusive leaf to a synthetic
event frame. The custom reader instead walks each event's actual call stack.
Below it assigns each sample to the closest selected frame, selecting auth,
usage, QoS, catalog/placement, document and ETag names. Percentages use all 87,022
CPU events, not server CPU or engine time. Unselected child calls, inlining,
async boundaries and symbol quality affect attribution. Inclusive stacks must
not be added together; these nearest-selected rows are also not method self CPU.

| Closest selected frame | CPU samples | Share of all captured CPU samples |
| --- | ---: | ---: |
| Usage middleware async callback | 5,194 | 5.969% |
| Project-check async callback `b__8_1` | 4,426 | 5.086% |
| Database-placement async callback | 3,694 | 4.245% |
| Usage `Transfer.Complete` | 2,206 | 2.535% |
| Usage `CountingStream.WriteAsync` | 2,007 | 2.306% |
| `ETags.Version` | 1,787 | 2.054% |
| Auth `Success` | 730 | 0.839% |
| `PlanAsync` | 410 | 0.471% |

17,464 actual leaves (20.068%) had an empty method name. Some native/kernel
symbol labels are implausible; no native-function ranking is claimed. This
trace prioritizes investigating usage accounting without removing it. It does
not prove that a native response-body feature is safe or faster. Any candidate
must preserve bytes, minute attribution, streaming/abort behavior and request
logging, pass real-HTTP integration coverage, then earn three quiet same-host
comparisons before a latency change can be claimed. Auth/revocation is unchanged.

All artifacts remain on delta under managed
`fix-api-token-hash-evidence-72ff07a7a739d3fe9331cba929d7f5d00528b4fe2ed287db776b1e3aba098dd3`,
`benchmark/bench/api-cpu-20261028-a/`: run script, binary identities, cells,
probe/collector logs, trace, built-in reports and analysis source/build/output.
`analysis-a` failed IDE0022; the corrected block-body analysis built with zero
warnings/errors in `analysis-b`. `analysis-c` added PID-scope verification,
also built with zero warnings/errors, and produced the final ranking. No guard
was suppressed and the benchmark was not rerun for these analysis corrections.

| Artifact | SHA-256 |
| --- | --- |
| CPU trace | `3fe872c9b7c58be63c79f551d6426b544b9dcd83440cde344d8a0082222f7820` |
| Final analysis source | `70ef5ca723a9be9f2ff1daf5965c81065d20a5a4f196d427d6845d22fcd8a52b` |
| Final ranking | `9603d60e2ebfaeaea1b0ddb427eb6526f7bd0498ae9b3146befcb3fd56b53364` |
