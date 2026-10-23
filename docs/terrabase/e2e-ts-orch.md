# TerraBase E2E against ts-orch

`tests/TerraBase.E2E` runs on in-process nodes by default. With `TERRABASE_E2E_ORCH_NODES` set, the same scenarios run
against the disposable ts-orch service `terrabase-e2e`: one node per region (gru, sjc, fra), host networking, client
listener on port 18080. The runner must be on the NetBird mesh (delta, mariogk-dev or beta).

## Variables

| Variable | Secret | Meaning |
|---|---|---|
| `TERRABASE_E2E_ORCH_NODES` | no | Each region's client listener: `gru=http://<netbird-ip>:18080,sjc=...,fra=...`. Setting it selects ts-orch. |
| `TERRABASE_E2E_ORCH_KEY` | yes | The e2e project's ts-auth project API key (scopes `databases:read databases:write documents:read documents:write`). |
| `TERRABASE_E2E_ORCH_REVISION` | no | The commit every node must report on its internal listener's `/healthz`. Set, the run is pinned: acceptance evidence. |
| `TERRABASE_E2E_ORCH_INTERNAL_NODES` | no | Pinned runs only: each region's internal listener, `gru=http://<netbird-ip>:<internal-port>,...`. The public listener never names the revision. |
| `TERRABASE_E2E_ORCH_PEER_SECRET` | yes | Pinned runs only: the service's peer credential (`TERRABASE_PEER_SECRET`), sent only to the internal listeners. |
| `TERRABASE_E2E_ORCH_OUTAGE` | no | `1` lets a pinned run stop regions (`make terrabase-e2e-orch-outage` sets it). |

## Credentials

The nodes trust the platform ts-auth issuer (`https://auth.terrascale.tech/terrascale`). The harness exchanges
`TERRABASE_E2E_ORCH_KEY` at `POST /terrascale/oauth/token` (client credentials, docs/auth/platform-contract.md 6.1) for a
15-minute project token and mints a new one two minutes before expiry, so a long outage run never sends an expired
token. One token covers documents, `/sql` and backups. The project and organization come from the token's `project_id`
and `org_id` claims. The harness creates the database `ci-e2e` (home gru, replicas sjc and fra) on first use, and each
test owns one collection in it.

A human creates the key once in the e2e project, as a project API key with the four scopes above. It is kept in the
runner's secret store and exported only into the run's environment. The key never goes on a command line, into a file
in the repository, or into a log. The harness sends it only to ts-auth, in an `Authorization: Basic` header.

## Outage proof

`make terrabase-e2e-orch-outage` runs `OrchOutageE2ETests` alone. For each region in turn it:

1. Removes the region by running `ts-orch service apply` on `ts-gru-1` (over ssh, `sudo -n`) with the service's own spec
   minus that region. The spec comes from `ts-orch service inspect terrabase-e2e` and travels on stdin. ts-orch removes
   the node and drops it from the peers files.
2. Writes, deletes and conflicts through both surviving regions.
3. Applies the spec with all three regions again, then waits until the node answers `/healthz` as itself.

Before the first stop, the spec must name `terrabase-e2e`, run in exactly gru, sjc and fra, and serve the nodes' port.
The target passes only when the run records exactly one actual pass on `ts-orch nodes`.

```sh
export TERRABASE_E2E_ORCH_NODES=gru=http://10.200.165.148:18080,sjc=http://10.200.109.193:18080,fra=http://10.200.40.238:18080
export TERRABASE_E2E_ORCH_REVISION=<commit the service runs>
# TERRABASE_E2E_ORCH_KEY comes from the runner's secret store
nice -n 19 make terrabase-e2e E2E_LABEL=orch-replication-matrix \
  TEST_ARGS='-- --treenode-filter "/*/*/*/*[Category=replication-matrix-live]" --minimum-expected-tests 13'
nice -n 19 make terrabase-e2e-orch-outage
```

Partitions, clock skew, file edits and process kills have no ts-orch switch. Those scenarios run on local nodes only and
skip on ts-orch, with the reason.
