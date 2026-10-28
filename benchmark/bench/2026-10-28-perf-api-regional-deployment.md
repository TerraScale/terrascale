# Regional deployment observation, 2026-10-28 09:59 UTC

Admitted ts-orch NetBird SSH and on-host peer-authenticated `/healthz` returned:

| Region | Reported revision | Health |
|---|---|---|
| gru | `6807e989ec6dbb80d54789510e90e095d5af871d` | ok |
| sjc | `86c7545afb7f335b954d2331b71121b097541af8` | ok |
| fra | `93b873792678c32fd14ae1aebc4fa9717eae79c3` | ok |

`git merge-base --is-ancestor` confirms key-watch commit `dc414b8a281aeb8d03a53f310e1a2a138350a6c7` in each revision. Storage format is unchanged at `18-e15d70682919`. [Allowlisted responses](2026-10-28-perf-api-production-0959.jsonl) retain timestamps and machine identity; credentials stayed on-host. No tenant or write was used.

This closes the earlier sjc revision gap, not authenticated regional parity or production latency. Storage recovery #813 is present in sjc only in this snapshot; [normal rollout](https://github.com/TerraScale/terrascale/actions/runs/40666990906/job/122744012719) was still running. The historical plan refusal remains unexplained, no new tenant was created, and the original server-over-engine target remains unmet.
