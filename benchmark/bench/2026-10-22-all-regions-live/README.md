# All-regions placement: live proof attempt (2026-10-23)

Target: TerraScale/terrascale#405 (`allRegions` placement), merged as `a269d44fdad537f9dd0fb15fc1590223a850301a`.
Image: `ghcr.io/terrascale/terrabase` index `sha256:868b90be19725552f71cbc68532d66185637e266725fbc7bb6a61d43480d5650`,
linux/arm64 child `sha256:463cf1e5b657378ad9705fd2c5a99277151bac16033371465bb1022977e565b7` (CI run 39880353377).

**Result: no live proof yet.** Neither the disposable cell (A) nor prod gru (B) reached the point where
`allRegions` could be observed. The in-process proof remains `tests/TerraBase.Fast/Replication/AllRegionsPlacementTests.cs`.

| Result | Value |
|---|---|
| Live all-regions proof | not done |
| Placement | not exercised |
| A: blocker | ts-sjc-1 and ts-fra-1 have no free reserved capacity for a second TerraBase service |
| B: state | prod gru runs a269d44f and does not become ready: catalog gate closed (`catalog_unverified`) |

Secrets are redacted throughout; no secret value was printed or stored.

## A. Disposable cell `terrabase-ar1`

### Reusing the existing test cell

`terrabase-e2e` (sjc, fra and a temporary `rt1-gru-test` gru VM, client port 28080) runs `eec12aee`, which does not
contain #405:

```
$ curl http://10.200.109.193:28080/healthz
{"status":"ok","region":"sjc","storageFormat":"10-8e53f392820a","revision":"eec12aee3ad2e780de3e6f6fec4a842a121ab406","machine":"ts-sjc-1"}
$ git merge-base --is-ancestor a269d44f eec12aee || echo "does NOT contain #405"
does NOT contain #405
```

### Own service

Test-only credentials were created for it: Vault `gru-1-terrabase-ar1-{peer-secret,master-key,backup-access-key-id,backup-secret-key}`
(random values, written from a 0700 temp dir through `--from-json file://`, never on argv), Tigris bucket
`terrascale-terrabase-ar1-test` and an Editor key scoped to it. The spec copies `terrabase-e2e`'s shape on ports
18080/18081/19091 (18081 is the NetBird-allowed internal port), with `"regions": ["sjc"]` first.

```
$ ssh ts-sjc-1 sudo -n ts-orch service apply -f ar1-sjc.json      # milliCPU 1000/500/250, memoryMiB 2048/1536/1024
REGION  RESULT                                                               READY  PHASES
gru     skipped-not-targeted                                                 5ms    -
sjc     ERROR service terrabase-ar1 is not ready: context deadline exceeded  -      -
fra     skipped-not-targeted                                                 6ms    -
$ ssh ts-sjc-1 sudo -n ts-orch service ps terrabase-ar1
TASK                       NODE  STATE    DESIRED  SINCE      RESTARTS  EXIT  ERROR
ooy0nachqapt2145oa14jlxvj        pending  running  1m53s ago  0         -     no suitable node (insufficient resources on 1 node)
```

ts-sjc-1 has 2 CPUs. `app-canary` and `whoami` declare no budget, so each reserves the 1 CPU / 4 GiB default, and
`terrabase-e2e` reserves 1 CPU. Even 0.25 CPU does not fit. ts-fra-1 runs the same services. The cell needs either a
host with free capacity or explicit small budgets on `app-canary` and `whoami`.

### Cleanup (all confirmed)

```
$ ssh ts-sjc-1 sudo -n ts-orch service rm terrabase-ar1
gru removed / sjc removed / fra removed
$ rm -rf /var/lib/ts-data/terrabase-ar1      # on ts-sjc-1 and ts-fra-1
$ tigris access-keys delete <terrabase-ar1-test key>; tigris buckets delete terrascale-terrabase-ar1-test
$ tigris buckets list | grep -c ar1-test; tigris access-keys list | grep -c terrabase-ar1-test
0
0
$ oci vault secret schedule-secret-deletion ...   # all four gru-1-terrabase-ar1-* secrets
SCHEDULING_DELETION, deleted 2026-10-24T01:39Z (OCI minimum: one day)
```

No database was created in any cell.

## B. Prod gru

Production `terrabase` is not deployed from main by CI: CI only publishes the image. Before the change, prod ran image
`sha256:1d91e807…` (revision `e7fa366d`, region `local`). At 2026-10-23T01:18:57Z the running spec was applied with only
the image changed (plus `"regions": ["gru"]`, 1500 milliCPU / 8192 MiB as in the live cgroup):

```
$ ssh ts-gru-1 sudo -n ts-orch service apply -f terrabase.json
gru     ERROR service terrabase update did not complete (UPDATING: update in progress)
sjc     skipped-not-targeted
fra     skipped-not-targeted
```

State at 2026-10-23T01:30Z (no further change was made):

```
$ ts-orch service ps terrabase
... starting ... not ready within 1m0s: task application readiness returned HTTP 503
$ curl https://api.terrascale.tech/healthz
{"status":"ok","region":"gru","storageFormat":"10-8e53f392820a","revision":"a269d44fdad537f9dd0fb15fc1590223a850301a","machine":"gru-1-ctl-1"}
$ curl https://api.terrascale.tech/readyz
{"status":"unavailable",...} HTTP 503
$ curl https://api.terrascale.tech/v1/platform/databases
{"error":{"code":"catalog_unverified","message":"this machine's catalog is not verified against the other machines yet"}} HTTP 503
```

Log: `This machine's catalog anchor is missing and its catalog is not a new cluster's: the catalog gate opens only by
pulls from trusted peers after reviewed recovery`. gru's ts-orch was updated at 2026-10-23T00:30Z and now gives the
task `TS_REGION`/`TS_NODE`/`TS_PEERS_FILE`. The node therefore runs as cluster member `gru-1-ctl-1`, not the old
standalone `local`, and its existing catalog has no anchor. This is not specific to #405. Bringing gru back is a
decision for the prod owner: re-apply the previous image `sha256:1d91e807…`, or perform a reviewed catalog recovery.
