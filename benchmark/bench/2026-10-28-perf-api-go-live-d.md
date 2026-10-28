# Live Go parity run d, 2026-10-28: rejected at database setup

There are no accepted latency cells and no SDK/raw comparison from this attempt.
A rejected setup is not a performance result. The original server-over-engine
<10% target remains unmet, and this run does not change its denominator.

| Step | Observation |
|---|---|
| Recovery condition | Busy write-intent retirement #813 released normally at 10:05:44 UTC (CI 40666990906, job 122744012719); root-reported 10:11 peer-authenticated health: gru/sjc/fra all `86c7545afb7f335b954d2331b71121b097541af8`, healthy, format `18-e15d70682919` |
| Admitted peers, 10:21:42 UTC via ts-gru-1 | 3 nodes, fra/gru/sjc, SHA-256 `3cd3cdd8a4f8f8cba6ed01009c56061be06a0f3468e80c25b48309a4e406067c`, mode 0600 |
| Auth region state, 10:21:56 UTC (root) | gru healthz 200 / readyz 200 `ready`, image `ghcr.io/terrascale/ts-auth@sha256:68232e89aa9bfb0b0583d6a27ac84dcf5f57d72b56184906153a841fb8273f9d`; sjc/fra namespaces had no running auth container. This does not establish public auth routing |
| Public auth, 10:27:33 UTC | `https://auth.terrascale.tech/healthz` 200, `/readyz` 200 |
| Source and build | Launcher `benchmark/scripts/go-parity-live-d-20261028.sh` at `7dba83deb44bfb1ca1ac23f59b665a99e961f5e5` (base origin/main `427ddf231`); `go1.27.3 linux/amd64`; tree `benchmark/clients/go` `061dfa2bc40cfad09d547c14775165f4266964f1`, `benchmark/internal` `294b343e16d4746b23e60bd20ef2246cb7e197d3`; go.mod `b0c03728…bfc313c`, go.sum `05493eb1…fc738` |
| Binary | `api-bench-go` SHA-256 `e123dae9f059fdce5fc8f4b1d5f75899dc8d9cafc6bb87e9a558e23615f5628c`; SDK `terrascale.tech/sdk => github.com/TerraScale/ts-go v0.0.0-20261028013942-07ffb51e1d66` (`07ffb51e1d66fb2f7a42673683e731bdfc26d158`) |
| Helpers | Tenant `api-overhead-tenant.py` SHA-256 `3f0352bb19aa22dc8de31816dd946a9c21e7a77b914dd8d66cc83fdea18aef54` (merged); plan classifier tracked here and installed operator copy both `4989a8aa57b8de3a108e98e2acd7e2ac43fb18bb23bb189c45d6d42345b2eb54`; observer `parity-host-cpu.py` `853761a1811804b78ddcb411092d33854b7a1df3afd982f6875ad667451bfe7e` |
| Preflight | Invalid credentials rejected with `parity credentials could not be loaded`, no output file created; fresh output label d; no retained `/tmp/api-overhead-*` directory |
| Quiet reservation | ts-heavy-any on delta held one slot; the launcher held the other 3 nonblocking |
| Tenant | Exactly one synthetic tenant created |
| Plan assignment, ~10:28:00 UTC | **Passed**: `Synthetic organization plan assigned` (direct operator helper on delta, admitted `ts-orch netbird ssh` to ts-gru-1) |
| Database setup, home `fra` | **Rejected**: `parity database create failed: timeout` by ~10:28:32 UTC. `timeout` is the runner's class for a context deadline or a net timeout. It does not say which hop timed out or whether the server applied the create |
| Cells | None run. No second tenant and no retry |
| Database cleanup | Owned name `api-perf-01m610ttq4n8ew5y4q25aj5sc2`: create `uncertain`, delete returned 404 → `absent`, `reconcile_required: true`, remote erasure unverified |
| Tenant cleanup | `Synthetic account and inbox cleanup settled.`; private directory removed; no `/tmp/api-overhead-*` directory remained after the run. Cleanup-only resume was not needed |

## CPU quiet control (one-second samples, % of one CPU, external to the run)

| Window (UTC, approximate from operator polling) | Samples | Mean | p95 | Max | External .NET builder samples ≥10% |
|---|---|---|---|---|---|
| Pre-plan 10:27:34–10:28:00 | 25 | 51.96 | 90.94 | 108.9 | 0 |
| Plan and setup 10:28:00–10:28:33 | 33 | 45.33 | 73.5 | 177.76 | 2 |
| Cleanup 10:28:33–10:31:03 | 149 | 96.64 | 364.86 | 1399.42 | 32 |
| **Whole run (gate)** | 207 | 83.07 | 249.27 | 1399.42 | 34 |

The existing gate (mean ≤25%, p95 ≤100%, no external builder ≥10%) **rejected**
the run. An outside .NET build server (`VBCSCompiler`/`dotnet`, started outside
the heavy-slot locks around 10:25 UTC) ran on delta. Even before it, the
background external mean was above 25%, mostly from interactive agent
processes. Local contention during the create cannot be excluded, so the
timeout cannot be attributed to the server.

## Limits

- Raw net/http wire replays remain unobservable (`RoundTripper attempts; net/http internal wire replays are not observable`).
- The original launcher's retained-directory check piped `find` into `grep` under
  `pipefail`, so a concurrent `find` race (one appeared here) could hide a
  match. A separate operator check found no such directory before or after the
  run. The follow-up below fixes the check; this fixed-output launcher must not
  be rerun for another attempt.
- Database deletion is only logical acceptance. Because the create is
  `uncertain` and the delete returned `absent`, the owned name above needs
  reconciliation in fra.

Private artifacts stay on delta (mode 0700/0600) under
`~/remote-builds/terrascale/fix-api-go-parity-live-d-d6faa6cac3cc2dde17bf87f074ef2a6860b715c30360dda10e5fc12e3560718c/benchmark/bench/go-parity-live-20261028-d/`.
SHA-256: cells.jsonl `e5fca7a8…3f4f`, run.log `e3c44dca…2994`,
cpu-processes.jsonl `c23a917e…1742`, quiet-summary.json `82dbd65a…1232`,
host.txt `cc191530…89a1dd`, run.sh `a9f5a67b…ee76`. No credential, recovery,
receipt, tenant, user or project content is included here.

## Launcher follow-up

Commit `c56e72c47` captures the complete directory enumeration before checking
for matches, refuses any matching entry (including links), and exits 74 on an
enumeration error. These shell controls and `bash -n` passed on delta through
`ts-heavy-any nice -n 19`; only the extracted guard ran, with a fake `find`.
No tenant launcher, credentials or network request was exercised by the controls.

| Guard control | Observed exit |
|---|---|
| Successful empty enumeration | 0 |
| Matching entry | 74 |
| Enumeration failure without output | 74 |
| Matching output followed by enumeration failure | 74 |

This correction does not reconcile the uncertain database or clear the failed
quiet-host gate. No new tenant attempt is authorized by these controls.
