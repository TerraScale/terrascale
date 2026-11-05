# Regional tails and CDC retention

Run each proof through `ts-heavy-any` from a dev host with the throwaway-account helper and the existing OCI node SSH access. Each invocation creates a separate account, gru-home database and sjc/fra replicas, then deletes the account in `finally`. Credentials stay in the helper's private state directory or travel over stdin. Output contains public measurements only.

```sh
TS_HEAVY_PARTITION=f9-tails TS_SLOT_LABEL=regional-shape-proof ts-heavy-any \
  bash -c 'F9F5_PROOF_TAG=f9-tails-<unique-tag> python3 benchmark/shape-tails/run.py tails' \
  --repo terrascale --branch main

TS_HEAVY_PARTITION=f5-load TS_SLOT_LABEL=bounded-cdc-proof ts-heavy-any \
  bash -c 'F9F5_PROOF_TAG=f5-load-<unique-tag> python3 benchmark/shape-tails/run.py load' \
  --repo terrascale --branch main
```

The tail proof runs a client on each replica node. It bootstraps through that region, then observes 30 gru updates and replays each answer. It also runs ten updates with a projected end-user grant in each region. Every tail response must be HTTP 200 and identify the client's region. Output includes offsets, serving machine, write-to-tail latency and local replay latency.

The load proof uses an account without shape subscriptions, so its CDC can drain after backup and replica acknowledgments. It runs three measured rounds of 1,024 updates with 1 KiB values, in sixteen transactions per round. Each round waits at most 120 seconds for the home node's retained-row gauge to return to baseline plus 256, and records backup metrics and the proof database's database/WAL file lengths. The gauge covers all open home databases; concurrent unrelated work can affect it. A failed drain is a failed proof and leaves its measurements in the output.

The floor integration test supplies the safety proof for live shapes, failed backup delivery, R3 pulls and the R7 registration seam. A consumer that stops advancing must retain its history; this benchmark never bypasses a floor.
