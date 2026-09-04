# TerraBase

TerraBase is TerraScale's Rust multi-Region database engine and public API.
Each member serves the REST and reactive APIs directly with axum and stores
one customer database per SQLite file. SQL is the only query language; writes
remain document operations, with canonical JSON bytes, extracted indexes,
causal replication, live result views, and Litestream backup.

The workspace [GOAL.md](../GOAL.md) is authoritative. This repository's
[GOAL.md](GOAL.md) is only the pointer above, while [AGENTS.md](AGENTS.md)
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
