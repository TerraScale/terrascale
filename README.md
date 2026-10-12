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
  CSS/JS sources are in `web/assets/`; served files are in `web/static/`.
- `tests/TerraScale.Dashboard.E2E/`: TUnit E2E journeys, test-only providers in `Demo/`.
- `scripts/`: dev/demo launchers, asset builds, SDK and E2E lint checks.
- `vendor/dotnet/`: the pinned ts-dotnet package ([provenance](vendor/dotnet/README.md)).

## Commands
Prefix every local build, test, lint and watcher with `nice -n 19`:

```sh
nice -n 19 make build      # locked Release build
nice -n 19 make lint       # CSS check, SDK/E2E guards, dotnet format
nice -n 19 make dev        # development host with hot reload
nice -n 19 make demo       # test-only demo host
nice -n 19 make test-e2e   # all TUnit E2E journeys
```
More: [development](docs/development.md), [migration status](docs/csharp-migration.md),
[zero-configuration contract](docs/zero-configuration.md).
