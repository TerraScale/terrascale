# TerraScale.Dashboard

Platform UI, MCP server and billing for TerraScale. Identity belongs to ts-auth;
persistent state lives in TerraBase through the official ts-dotnet SDK
(`TerraScale.DotNet`). It is a .NET 11 application using RazorSlices and Datastar.

The C# application is the only implementation; the Go application was removed.
Production runs on OCI gru under ts-orch; see the
[OCI deployment contract](oci-deployment.md).

Connect an assistant with the [MCP guide](https://docs.terrascale.tech/guides/mcp).
The [dashboard integration API](platform-api.md) describes the serving
session-authenticated contract and links its interactive reference.

## Layout
- `src/TerraScale.Dashboard/`: the application (`Features/`, `Platform/`, `UI/`).
  CSS/JS sources are in `web/assets/`, feature CSS sits beside its slice in `Features/`; served files are in `web/static/`.
- `src/TerraScale.Dashboard/Demo/`: in-memory providers for `DEMO=true` and the E2E suite.
- `tests/TerraScale.Dashboard.E2E/`: TUnit E2E journeys; scenario and protocol controls in `Demo/`.
- `scripts/dashboard/`: dev/demo launchers, asset builds, SDK and E2E lint checks.
- `sdk/dotnet/`: the official SDK source, consumed from the same monorepo revision through ProjectReference.

## Commands
Run builds and tests through the shared dev-host runner from the monorepo root:

```sh
ts-heavy-any make dashboard-build --repo terrascale --branch main
ts-heavy-any make dashboard-test-fast --repo terrascale --branch main
ts-heavy-any make dashboard-check --repo terrascale --branch main
ts-heavy-any make dashboard-test-slow --repo terrascale --branch main
bash scripts/dashboard/dev.sh   # development host with hot reload
bash scripts/dashboard/demo.sh  # local synthetic demo host
```
## Demo mode
`DEMO=true` (exactly) makes the one image serve the public synthetic demo: in-memory
providers instead of TerraBase, ts-auth and Stripe, with admin, MCP and destructive
routes blocked. Production uses ts-orch's persistent service contract.

## OCI production
Production needs `TS_DASHBOARD_TERRABASE_URL` (TerraBase's private listener, `http://127.0.0.1:8081`) and
listens on `ASPNETCORE_URLS` (`http://127.0.0.1:8085` in the ts-orch service spec); the loopback TLS edge sends `X-Forwarded-Proto` and `X-Forwarded-For`.
Each secret may instead arrive as a file named by `<NAME>_FILE` (ts-orch delivers OCI Vault secrets that way).

More: [development](development.md), [migration status](csharp-migration.md),
[zero-configuration contract](zero-configuration.md).
