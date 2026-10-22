# TerraScale.Dashboard

Platform UI, MCP server and billing for TerraScale. Identity belongs to ts-auth;
persistent state lives in TerraBase through the official ts-dotnet SDK
(`TerraScale.DotNet`). It is a .NET 10 application using RazorSlices and Datastar.

The C# application is the only implementation; the Go application was removed.
Production rollout is blocked on identity delivery, ts-auth and discovery: the
app is not deployed and exits at startup without its identity file. See the
[runtime integration gates](docs/csharp-migration.md#runtime-integration-gates).

## Layout
- `src/TerraScale.Dashboard/`: the application (`Features/`, `Platform/`, `UI/`).
  CSS/JS sources are in `web/assets/`, feature CSS sits beside its slice in `Features/`; served files are in `web/static/`.
- `src/TerraScale.Dashboard/Demo/`: in-memory providers for `DEMO=true` and the E2E suite.
- `tests/TerraScale.Dashboard.E2E/`: TUnit E2E journeys; scenario and protocol controls in `Demo/`.
- `scripts/`: dev/demo launchers, asset builds, SDK and E2E lint checks.
- `vendor/dotnet/`: the pinned ts-dotnet package ([provenance](vendor/dotnet/README.md)).

## Commands
Prefix every local build, test, lint and watcher with `nice -n 19`:

```sh
nice -n 19 make build      # locked Release build
nice -n 19 make lint       # CSS check, SDK/E2E guards, dotnet format
nice -n 19 make dev        # development host with hot reload
nice -n 19 make demo       # local in-memory demo host
nice -n 19 make check      # required build, lint, security and Fast
nice -n 19 make test-slow  # independent, non-blocking Slow journeys
```
## Demo mode
`DEMO=true` (exactly) makes the one image serve the public synthetic demo: in-memory
providers instead of TerraBase, ts-auth and Stripe, with admin, MCP and destructive
routes blocked. ts-orch deploys main's GHCR image to https://dash.terrascale.tech.

```sh
fly secrets set DEMO=true --app ts-dashboard    # serve the demo
fly secrets unset DEMO --app ts-dashboard       # serve the real application
```

## Off Fly
Production off Fly needs `TS_DASHBOARD_TERRABASE_URL` (TerraBase's private listener, e.g. `http://127.0.0.1:8081`) and
listens on `ASPNETCORE_URLS` (default port 8080); a loopback TLS edge must send `X-Forwarded-Proto` and `X-Forwarded-For`.
Each secret may instead arrive as a file named by `<NAME>_FILE` (ts-orch delivers OCI Vault secrets that way).

More: [development](docs/development.md), [migration status](docs/csharp-migration.md),
[zero-configuration contract](docs/zero-configuration.md).
