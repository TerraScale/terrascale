# TerraBase

The workspace [GOAL.md](../GOAL.md) is the sole authority for TerraScale and this repository is its Rust multi-Region database engine and public API: SQLite is the only storage engine, SQL is the only query language, and the member serves REST and reactive APIs directly while replication and sync follow the current workspace decisions. Repository rules live in [AGENTS.md](AGENTS.md), durable local context is indexed in [docs/adr/README.md](docs/adr/README.md), and all implementation work follows those authorities.
