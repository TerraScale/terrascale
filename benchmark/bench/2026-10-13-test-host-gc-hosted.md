# Hosted alpha test GC qualification, 2026-10-13

[PR #212](https://github.com/TerraScale/terrascale/pull/212) merged as `745ea0d5` at20:11:15Z after its frozen head `48854928` passed the required `test`. [Own CI run](https://github.com/TerraScale/terrascale/actions/runs/38458866836) confirms `cpus=8`, `ServerGC=True`, `HeapCount=8`, and `GCDynamicAdaptationMode=0`. The existing shared BuildKit and cache wiring remain; both .NET settings arrive through the Docker test ancestry. Production stages remain independent.

| Runtime observation | Before: #207 attempt1, 44 CPUs/heaps | After: #212, 8 CPUs/heaps |
|---|---:|---:|
| GC pause at119s | 88.947s /119.076s (74.697%) | 7.677s /119.015s (6.450%) |
| Final cumulative GC pause | 403.741s /708.645s (56.974%) | 8.190s /156.446s (5.235%) |
| Host load near119s | 47.07 | 19.85 |
| TerraBase Fast suite duration | Failed; final sample708.645s | Passed1567/1567;158.075s |
| Fast failures | 10, including six two-minute timeouts | 0;0 skipped |

| CI job | Before #207 attempt1 | After #212 | After result |
|---|---:|---:|---|
| changes | 9s | 8s | Success |
| libs | Skipped | 291s | Endpoints88/88, Logging46/46 |
| auth | 729s | 549s | Fast664/664;245.904s suite |
| terrabase | 1090s | 564s | Fast1567/1567; native6/6 |
| test | 9s | 17s | Success |

Job times include checkout, build and checks, from GitHub's started_at/completed_at. The #212 run took801s from creation to its final update, including queues. Before uses the original failed [TerraBase job](https://github.com/TerraScale/terrascale/actions/runs/38447780403/job/115470667881) and the attempt1 job API; the same run was later rerun, so its latest job list must not replace this baseline.

These hosted runs use different source revisions and shared-host loads. The observed pause near119s fell91.4%; this is a resulting-CI comparison, not an isolated causal estimate. The prior [same-source local comparison](2026-10-13-test-host-gc.md) passed both ways and measured15.7% shorter Fast duration and61.3% less cumulative GC pause, also with differing host load.

[Allowlisted hosted samples](2026-10-13-test-host-gc-hosted.json) preserve the runtime configuration and numeric observations. No raw service output or credential values are included. Auth, libs and native checks passed with zero skips. Slow, preview, deploy and e2e were skipped on this PR event; this does not establish main Slow/e2e green, a working preview, production runtime CPU count2, or merge-to-three-region deployment time. Those qualifications remain pending under their owners.
