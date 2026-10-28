# Regional release observation, 2026-10-28 10:59 UTC

The normal release for main `6931b0b57cef86e7852966409238a734f0584b45`
succeeded at 10:53:26 UTC. Fresh peer-authenticated health reads then observed
that revision healthy in gru, sjc and fra. It includes #821's shared snapshot
pull fix and #795's key-watch change, verified by git ancestry.

| Evidence | Observation |
|---|---|
| CI run | [40673744931](https://github.com/TerraScale/terrascale/actions/runs/40673744931) |
| Release job | [122767008083](https://github.com/TerraScale/terrascale/actions/runs/40673744931/job/122767008083), `main (terrabase)`, success |
| Release step | `Release to every region`, 10:49:11–10:53:26 UTC, success |
| Required aggregate check | `test`, success |
| Slow status at observation | `slow-terrabase-main` still running; non-blocking and not claimed passed |

| Observed UTC | Region | SSH alias | Service machine | Health | Revision | Storage format |
|---|---|---|---|---|---|---|
| 10:59:49 | gru | ts-gru-1 | ts-gru-2 | ok | 6931b0b57cef86e7852966409238a734f0584b45 | 18-e15d70682919 |
| 10:59:53 | sjc | ts-sjc-1 | ts-sjc-1 | ok | 6931b0b57cef86e7852966409238a734f0584b45 | 18-e15d70682919 |
| 10:59:57 | fra | ts-fra-1 | ts-fra-1 | ok | 6931b0b57cef86e7852966409238a734f0584b45 | 18-e15d70682919 |

Reads used admitted `ts-orch netbird ssh`. On each host the probe selected the
single running TerraBase container, kept its existing peer credential on-host,
and queried that container's own admitted address on the internal health
listener. Only allowlisted health fields returned. The SSH alias is recorded
separately from the service's reported machine; this is no claim about all host
aliases or all cluster members. No tenant, mutation, private recovery material,
new secret retrieval or benchmark was used. Nothing ran on beta.

The exact executed probe is archived at
`benchmark/scripts/api-regional-release-1059-20261028.py`. It has a fixed,
exclusive output filename: preserve it as historical provenance rather than
rerunning it for a new observation. The safe returned JSON is
`2026-10-28-perf-api-regional-release-1059.json`.

| Artifact | SHA-256 |
|---|---|
| Probe | b6f7b89d9c443eea06f966e6fd7c0b146498eb6bda51dbfa4ef4cd1d2e3bfffb |
| Returned JSON | e069306e47f80111afc9111e0285fd8323de7fc58f399019fb346bfbca5c3394 |

Main had advanced to `a5999f5ea` (#777); this snapshot does not establish its
deployment. Health does not prove authenticated database create/read readiness,
a quiet host, the past create timeout's cause or physical erasure. The owned
database `api-perf-01m610ttq4n8ew5y4q25aj5sc2` remains unresolved and
`reconcile_required`. No new tenant attempt is cleared. There are no latency
cells or performance claims; the original <10% target and denominator remain.
