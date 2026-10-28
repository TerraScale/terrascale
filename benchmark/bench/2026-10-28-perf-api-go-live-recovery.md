# Auth recovery and rejected Go setup, 2026-10-28

No accepted latency cell or SDK/raw comparison exists from this attempt.
The original server-over-engine target remains unmet.

| Recovery evidence | Observation |
|---|---|
| Normal auth release | Main run [40661010360](https://github.com/TerraScale/terrascale/actions/runs/40661010360), `main (auth)` and `Release to every region` passed |
| Running gru auth, 09:14 UTC | Image `sha256:0f492bc4334a067b0fc1bdf9163b10c1c3391f417676e4518d0de0fac3145bd5`; health/ready 200 |
| Previously retained run-b tenant | Helper completed with `Synthetic account and inbox cleanup settled.`; private recovery directory confirmed absent |
| New admitted peers snapshot, 09:17:54 UTC | 3 nodes; fra/gru/sjc; SHA-256 `3cd3cdd8a4f8f8cba6ed01009c56061be06a0f3468e80c25b48309a4e406067c` |
| New attempt's local prerequisites | Current Go client built, binary hashed, invalid credentials rejected before output creation, new output directory, three additional heavy slots reserved |
| New attempt's setup | Synthetic organization plan assignment failed before database setup; no latency cells |
| Initial account cleanup | Transport failure; private recovery material retained |
| Receipt-only reconciliation | HTTP 202, deletion started; no continuation authorization retained |
| Subsequent bounded cleanup | Helper completed with `Synthetic account and inbox cleanup settled.`; private recovery directory confirmed absent |

The helper's cleanup code matched merged source SHA-256
`3f0352bb19aa22dc8de31816dd946a9c21e7a77b914dd8d66cc83fdea18aef54`.
The plan helper reports only failure, without its HTTP status or cause; this
evidence cannot distinguish route, storage or transport refusal. Another tenant
must not be created merely to repeat the same undiagnosed setup.

| TerraBase authenticated health, 09:14 UTC | Revision | Storage format |
|---|---|---|
| gru, `ts-gru-2` | `dc414b8a281aeb8d03a53f310e1a2a138350a6c7` | `18-e15d70682919` |
| sjc, `ts-sjc-1` | `8f398b3291551da11fe3dc0da2de17ead5e165b5` | `18-e15d70682919` |
| fra, `ts-fra-1` | `f9404808aa8a25d22c7a72a342d9988579e37482` | `18-e15d70682919` |

All three health responses said ok. sjc is still before key-watch #795, so this
does not establish all-region deployment of that measured reduction. The checked
sjc/fra namespaces had no running auth container; that does not establish public
auth routing.

Run source `3adbb18f2`; binary SHA-256
`f445dc51d993f7b5e5a23248319eae8481859f3ff97f160fc8e70e62502bf6ae`.
All build/run work used `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash
benchmark/scripts/go-parity-live-recovery-20261028.sh --repo terrascale --branch
fix/api-go-parity-live-recovery`. Across 81 one-second CPU observations,
external mean/p95/max were 4.73/10.89/60.39% of one CPU, with no external
compiler/test at >=10%; the existing quiet control passed. These observations
cover setup and initial cleanup, not an API measurement window.

Beta retains source, binary hash, peer provenance, setup log and CPU observations
under
`~/remote-builds/terrascale/fix-api-go-parity-live-recovery-0f81d9d56d3f79067155e39dc3a7f7d6b91f79b8572773762de6457d0f6fff9b/benchmark/bench/go-parity-live-20261028-c/`.
No credential or private recovery content is included here.

Read-only follow-up at 09:34 UTC returned HTTP 405 `method_not_allowed` for GET
on the PUT-only internal plan route in all three regions. No organization or
plan was changed. This establishes current route/authentication availability,
not the failed request's status or cause. Raw allowlisted observations:
[plan route](2026-10-28-perf-api-plan-route.jsonl).

At 09:38 UTC gru served `6807e989ec6dbb80d54789510e90e095d5af871d`; sjc and
fra still served the revisions above, all health ok with the same storage format
([snapshot](2026-10-28-perf-api-production-0938.jsonl)). The current gru
container's log starts at 09:33. Five retained TerraBase container records were
checked, but none of their readable log windows covers setup at 09:19:35–09:20:57
([coverage](2026-10-28-perf-api-plan-log-coverage.jsonl)). Their lack of matching
failures cannot exclude a cause. Diagnostic scripts are retained under
`benchmark/scripts/api-overhead-plan-{probe,logs}-20261028.py`; credentials stay
on-host and raw messages/identifiers are never exported.
