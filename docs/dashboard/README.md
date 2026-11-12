# TerraScale.Dashboard

Platform UI, MCP server and billing for TerraScale. Identity belongs to ts-auth;
persistent state lives in relational tables of its TerraBase database, read and
written through the TerraBase EF Core provider ([state storage](state-storage.md)).
TerraBase's product APIs (database management, SQL, backups, usage) are reached
over MagicOnion through the shared contracts library. It is a .NET 11
application using RazorSlices and Datastar.

The C# application is the only implementation; the Go application was removed.
Production runs on OCI gru under ts-orch; see the
[OCI deployment contract](oci-deployment.md).

[Agent databases](agent-databases.md) describes TTL, forks and the database MCP lifecycle.
[Database placement](database-placement.md) describes how a database's regions are chosen and saved.
[Sign-in flow](auth/sign-in-flow.md) describes the OIDC flow, its lifetime and the callback failure pages.
[Personal access tokens](auth/access-tokens.md) describes what a token's expiry means and what ends it earlier.

SDK language tabs defines the shared dashboard, docs and landing control.

Connect an assistant with the [MCP guide](https://docs.terrascale.tech/guides/mcp).
The [dashboard integration API](platform-api.md) describes the serving
session-authenticated contract and links its interactive reference.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Layout
- `src/TerraScale.Dashboard/`: the application (`Features/`, `Platform/`, `UI/`).
  CSS/JS sources are in `web/assets/`, feature CSS sits beside its slice in `Features/`; served files are in `web/static/`.
- `scripts/dashboard/`: the development launcher, asset builds and lint checks.

## Commands
Run from the monorepo root:

```sh
make dashboard-build
make dashboard-lint
make dashboard-check
bash scripts/dashboard/dev.sh   # development host with hot reload
```

The journey in `e2e/` is the only test ([`e2e/AGENTS.md`](../../e2e/AGENTS.md)).
The production image always uses ts-orch's persistent service contract.

## OCI production
Production needs `TS_DASHBOARD_TERRABASE_URL` (TerraBase's private listener, `http://127.0.0.1:8081`) and
listens on `ASPNETCORE_URLS` (`http://127.0.0.1:8085` in the ts-orch service spec); the loopback TLS edge sends `X-Forwarded-Proto` and `X-Forwarded-For`.
Each secret may instead arrive as a file named by `<NAME>_FILE` (ts-orch delivers OCI Vault secrets that way).

More: [development](development.md), [state storage](state-storage.md), [state storage](state-storage.md),
[zero-configuration contract](zero-configuration.md), [metric definitions](metrics.md).
