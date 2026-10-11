# TS-761 live-run prerequisites (2026-10-08)

| Region | Point reads | Mixed 70/30 | Ramp | CPU / memory | TerraBase results |
| --- | --- | --- | --- | --- | --- |
| gru | unavailable | unavailable | unavailable | unavailable | not stored |
| sfo | unavailable | unavailable | unavailable | unavailable | not stored |

**No live capacity benchmark was run.** These are prerequisite observations,
not zero throughput, a failure-rate estimate or a performance verdict.

| Prerequisite | Verified evidence | Needed next |
| --- | --- | --- |
| Official Go SDK JWT data client | `ts-go` main `05ae299` and ULID revision `49fb07f5b86680037ddf79eac907679ae7d5b8b2` still require `tb_` in `v1.New`; `v1.Config` has no token-source surface | Public JWT project data client and lifecycle/index methods |
| Plain ULID SDK boundary | [ts-go PR #35](https://git.terrascale.tech/TerraScale/ts-go/pulls/35) is unmerged at inspection | Admit the official plain-ULID SDK revision |
| Project authority | TerraBase main `f5a862b1` rejects project JWTs on compatibility management routes; ts-auth's platform contract says unscoped service tokens have no tenant authority | Dedicated benchmark project OAuth grants, not a legacy data key |
| OCI Vault client | Current GRU `gru-1-ts-auth-service-clients` was read in memory; only the identifier `ops-smoke` was exposed, never the secret | Provision dedicated project-scoped benchmark credentials in Vault |
| Cluster metrics | No TS-761 slice 4 thread/API was available in the orchestrator project at inspection | Consume its actual merged API; meanwhile record unavailable |

The attempted compatibility-key implementation was discarded before commit.
There is no direct REST data adapter, header-rewriting SDK transport, invented
metrics API or fabricated report. Local race tests qualify scheduler/protocol
accounting only; they do not qualify deployed TerraBase capacity.
