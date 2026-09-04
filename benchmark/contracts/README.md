# TerraBase contracts (E-1)

TerraBase is the source of truth for every cross-repository contract. This
directory holds the published artifacts; consumers pin `CONTRACT_VERSION`
and regenerate with `make contracts`. Additive-only until launch.

| Artifact | Source | Status |
| --- | --- | --- |
| `openapi.json` | axum routes via `terrabase_server::api::openapi_json` | generated |
| `manifest.json` | reactive MessagePack frames (`reactive_api.rs`) | stub: decoder generation arrives with full E-1 |
| `protos/` | `crates/terrabase-tbep/proto` (verbatim copy) | stub: registry publish arrives with full E-1 |
| `terrabase-go/` | `buf generate` from the protos | stub: generated stubs arrive with full E-1 |

```bash
make contracts   # regenerate everything in place
./scripts/validate-release-contracts.sh   # fail closed on drift
```
