# TerraBase

TerraBase is TerraScale's Rust multi-Region database engine and public API.
Each member serves the REST and reactive APIs directly with axum and stores
one customer database per SQLite file. SQL is the only query language; writes
remain document operations, with canonical JSON bytes, extracted indexes,
causal replication and live result views. Production backup composition uses
the internal native Rust subsystem. Its
recovery, provider, and performance qualification remains incomplete. Backup
policy selects OCI Object Storage, Tigris, or both by plan, with
operator-managed overrides for specific users. See [ADR 0046](docs/adr/0046-native-rust-backup-subsystem.md).

The workspace [GOAL.md](../GOAL.md#terrabase) defines the production target; [AGENTS.md](AGENTS.md)
contains repository rules and [docs/adr/README.md](docs/adr/README.md) indexes
durable local decisions. `GlobalDurable` is accepted only with at least two
active selected Regions; a request with fewer Regions fails before
acknowledgement.

## Runtime boundary

```text
customer SDK / dashboard
        -> public REST + reactive API over HTTPS (axum)
        -> TerraBase member (SQLite, sync, replication)
orchestrator / CLI
        -> private protobuf/gRPC TBEP over HTTP/2 and mTLS
```

TBEP is private control traffic. The member owns document storage, indexing,
query execution, causality, CRDT merge, replication, repair, backup, restore,
operations, reactive state, and usage facts. Cloud development uses the
hosted branch workflow; the engine binary is not distributed for self-hosting.

## Honest implementation status

The Rust engine, SQLite storage, query, indexing, replication, sync, backup,
and recovery modules are under active qualification. Source and tests describe
implemented behavior; local tests, fixtures, and benchmark tables do not by
themselves qualify an OCI or multi-Region production deployment. The current
launch blockers and required evidence are tracked in the workspace
[GOAL.md](../GOAL.md).

## Development

Use the repository check target for local verification:

```sh
make check
```

It runs formatting, Clippy, workspace tests, protobuf lint, contract checks,
and the `terrabase-go` client checks when that client is present. Run
`make contracts` when changing the published contract. See
[docs/development.md](docs/development.md) for the contributor workflow.

TerraBase is licensed under the [MIT License](LICENSE.md).

## TerraScale.Endpoints

The C# HTTP surface uses the `TerraScale.Endpoints` components embedded in the
single TerraBase project under `src/TerraBase/Endpoints`. Endpoint classes live in individual files and can
be mapped with:

```csharp
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly);
var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
```

Discovery scans an assembly once, caches the deterministically ordered endpoint
types, and activates them through dependency injection. Services that need zero
reflection at startup can pass a precomputed endpoint type list instead. The
integration serves OpenAPI at `/openapi/v1.json` and Scalar at `/scalar`.

Edge-case coverage is in
[`tests/TerraScale.Endpoints.Tests`](tests/TerraScale.Endpoints.Tests).
