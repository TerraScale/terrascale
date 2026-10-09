# Rollout: cross-region replication gru-1 ↔ sfo-1 (TS-643)

The steps that turn on [cross-region replication](../cross-region.md) between gru-1 and sfo-1 and set up the check
COS runs every 20 minutes. Run them in order; each ends with what to see before going on.

## 1. Prerequisites (ts-orchestrator, TS-668)

- The gru↔sfo NATS interregion link is up. The existing contract already carries `ts.orch.v1.<cell>.query.>`.
- The terrabase role in both cells may:
  - subscribe to `ts.orch.v1.<own cell>.query.terrabase-wan.>`;
  - publish to `ts.orch.v1.*.query.terrabase-wan.>`;
  - answer requests (`allow_responses`).
- sfo-1 members have their NATS role identity (`TERRABASE_NATS_*`).
- sfo-1 serves its public API (TS-662).

## 2. Profiles

Add the same setting to both cells' TerraBase profiles and release them (root-signed cell spec amendment):

```text
TERRABASE_WAN_REGIONS=gru-1=gru,sfo-1=sfo
```

Members load a profile only with their next release (`operator terrabase-release --profile`, the profile from the cell
spec). What the first live release (2026-10-08) ran into:

- `invalid delivery input: artifact object did not become visible`: the cell's artifact store (`OBJ_TS_ARTIFACTS`) is
  full. Delete superseded TerraBase binaries that no member runs.
- `resolve rollback artifact …: durable artifact claim signature`: the members run a CI-released digest, and the
  operator config trusts only its own signer. Release with a copy of the config whose `controller.trusted_signers` also
  holds the cell spec's `ci-release` signer.

On one member of each region, the admin listener should then show the other region's members and no lag:

```sh
curl -s http://127.0.0.1:9464/metrics | grep -E '^terrabase_wan_(members|lag|staleness_seconds|failures_total)'
# terrabase_wan_members{region="sfo-1"} 3      (on gru; region="gru-1" on sfo)
# terrabase_wan_lag{region="sfo-1"} 0
```

`terrabase_wan_members` at 0, or a growing `terrabase_wan_failures_total{reason="heads"}`, means the link or the grants
are missing: check step 1.

## 3. The canary database

The check needs a database of its own (TS-660's in-region check owns its canary), at home in gru-1 with a replica in
sfo-1 ([placement](../cross-region.md#placement)).

1. Choose canonical ULIDs once, using the maintained `python-ulid` package in your tooling environment:

   ```sh
   ulidgen() { python3 -c 'from ulid import ULID; print(ULID())'; }
   ORG=$(ulidgen) PROJECT=$(ulidgen) DB=$(ulidgen) ACTOR=$(ulidgen)
   ```

2. Register the organization and project in **both** regions under the **same** ids. On each region's catalog primary
   (gru-1-ctl-1, ts-sfo-1), send to the loopback administrative listener; `X-TerraBase-User-Id` becomes the owner:

   ```sh
   cat > project.json <<JSON
   {"id":"$(ulidgen)","ops":[
     {"op":"create_organization","id":"$ORG","name":"cross-region-canary"},
     {"op":"create_project","organization":"$ORG","id":"$PROJECT","name":"cross-region-canary"}]}
   JSON
   curl -s http://127.0.0.1:9464/v1/management/commits -H 'Content-Type: application/json' \
     -H "Authorization: Bearer $(cat "$TERRABASE_MANAGEMENT_CREDENTIAL_FILE")" -H 'X-TerraBase-User-Id: $ACTOR' -d @project.json
   ```

   The credential is the file the member's `TERRABASE_MANAGEMENT_CREDENTIAL_FILE` names (read it from the process
   environment); both profiles set it.

3. Create the canary on gru-1-ctl-1 only, with sfo-1 as its replica; sfo-1 records it from gru's heads within seconds:

   ```sh
   curl -s http://127.0.0.1:9464/v1/management/commits -H 'Content-Type: application/json' \
     -H "Authorization: Bearer $(cat "$TERRABASE_MANAGEMENT_CREDENTIAL_FILE")" -H 'X-TerraBase-User-Id: $ACTOR' \
     -d "{\"id\":\"$(ulidgen)\",\"ops\":[{\"op\":\"create_database\",\"organization\":\"$ORG\",\"project\":\"$PROJECT\",
          \"name\":\"cross-region-canary\",\"id\":\"$DB\",\"replica_regions\":[\"sfo-1\"]}]}"
   ```

4. On the same two members, issue one API key per region. Keys are per region, and the answer holds `secret`:

   ```sh
   curl -s http://127.0.0.1:9464/admin/api-keys -H 'Content-Type: application/json' -d "{\"name\":\"cross-region-canary\",
     \"scopes\":[\"documents:read\",\"documents:write\"],\"organizationId\":\"$ORG\",\"projectId\":\"$PROJECT\",\"databaseId\":\"$DB\"}"
   ```

## 4. The check

Write the canary file where COS runs the check, mode 0600:

```json
{"project": "<PROJECT>", "database": "cross-region-canary",
 "regions": [{"region": "gru-1", "api": "https://gru.api.terrascale.tech", "secret": "<gru key>"},
             {"region": "sfo-1", "api": "https://sfo.api.terrascale.tech", "secret": "<sfo key>"}]}
```

```sh
install -m 0600 /dev/null ~/.config/terrabase/cross-region-canary.json   # then fill it in
scripts/cross-region-check.py --canary ~/.config/terrabase/cross-region-canary.json --lag-threshold 60
```

- **What it checks.** Per direction, a write through one region's API must be readable through the other's, with the same
  version, within the threshold. Then one document written in both regions at once must converge to the same winner.
- **Output.** One JSON line, with the lag measured per check.
- **Exit codes.** `0` when everything passed, `1` on a failure, `2` when the canary file is missing, unreadable or
  readable by others.

COS runs it every 20 minutes and alerts on a non-zero exit.
