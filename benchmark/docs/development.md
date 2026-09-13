# Development contract

The workspace [GOAL.md](../../GOAL.md) is the product and architecture
authority;
[docs/adr/README.md](adr/README.md) indexes durable local context, and
[AGENTS.md](../AGENTS.md) contains the repository rules. Source and tests are
the implementation authority.

TerraBase is Rust with SQLite as its only storage engine. The member serves
the public API directly; TBEP is private control traffic. SQL is the only
query language, document writes retain causal and history semantics, and the
current replication and sync designs are ADR 0044.

Run the same solution build and test locally that CI runs:

```bash
nice -n 19 dotnet build TerraBase.slnx --configuration Release
nice -n 19 dotnet test TerraBase.slnx --configuration Release --no-build
```

When changing the published OpenAPI, protobuf, or MessagePack contract, update
the checked-in contract archive and regenerate consumer output in the affected
SDK repository. Do not hand-edit generated output. Keep changes small, remove
retired code and its documentation in the same series, and update links when a
document is removed.

Rust work should use the lowest available CPU priority. The member uses
bounded asynchronous I/O and bounded CPU parallelism derived from admitted
resources; blocking work must not run on executor threads. Retain
deterministic results, durability, authorization, tenant isolation, fairness,
backpressure, and recovery while changing performance-sensitive code.
