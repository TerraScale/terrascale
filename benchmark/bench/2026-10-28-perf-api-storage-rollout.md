# Normal storage rollout verified, 2026-10-28 10:11 UTC

[Main run 40666990906](https://github.com/TerraScale/terrascale/actions/runs/40666990906/job/122744012719) completed `Release to every region` successfully at 10:05:44 UTC. No manual deployment or reset was used.

Fresh on-host peer-authenticated `/healthz` probes through admitted ts-orch NetBird SSH report **gru, sjc and fra healthy at `86c7545afb7f335b954d2331b71121b097541af8`**, including busy write-intent retirement #813 and key-watch #795. Storage format remains `18-e15d70682919`. [Allowlisted responses](2026-10-28-perf-api-production-1011.jsonl) include timestamps and machine identities; credentials stayed on-host.

This replaces the partial-rollout state in the [09:59 snapshot](2026-10-28-perf-api-regional-deployment.md). Health does not prove plan assignment, authenticated document parity or latency. The historical setup refusal remains unexplained; no new tenant or write was used in this verification. The original server-over-engine target remains unmet.
