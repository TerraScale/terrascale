# TerraScale

TerraScale is one monorepo for the platform: **TerraBase** (the multi-region SQL database), **ts-auth** (hosted identity provider), **TerraScale.Dashboard** (platform UI/API/MCP), shared .NET libraries (`TerraScale.Endpoints*`, `TerraScale.Logging`), the C# Aspire local host (`TerraScale.Aspire`), the five SDKs in `sdk/` (.NET, Go, JS, Python, Rust), the public sites (`docs-site/`, `landing-site/`, `status-site/`), the end-to-end journey (`e2e/`) and benchmarks (`benchmark/`).

Contributor and agent rules live in [`AGENTS.md`](AGENTS.md).

## Layout

| Path | What |
|---|---|
| `src/` | .NET services, libraries, vendored storage driver (`src/vendor/`) and local Aspire host (`src/TerraScale.Aspire`) |
| `sdk/` | Customer SDKs: `dotnet`, `go`, `js`, `python`, `rust` |
| `e2e/` | The journey, the only test: Playwright against the real services and all five SDKs ([`e2e/AGENTS.md`](e2e/AGENTS.md)) |
| `docs-site/`, `landing-site/`, `status-site/` | Public Cloudflare Workers sites |
| `benchmark/` | Go benchmarks, .NET library benchmarks (`benchmark/dotnet/`) and results (`benchmark/bench/`) |
| `ops/` | Secret inventory (`ops/secrets/secrets.json`) |
| `.github/` | CI/CD workflows, actions and helper scripts (`.github/scripts/`) |

## Commands

Run targets from the repository root (`Makefile`):

| Command | What |
|---|---|
| `make dev` | Start TerraBase, ts-auth and Dashboard locally under Aspire (`src/TerraScale.Aspire`) |
| `make build` | Restore `TerraScale.slnx`, fetch pinned native libraries and build |
| `make check` | Formatting, full build, libs style, auth audit + OpenAPI lint, dashboard check and benchmark check |
| `make libs-build`, `make libs-check` | Shared .NET libraries |
| `make auth-build`, `make auth-check` | ts-auth build, NuGet audit and OpenAPI lint |
| `make terrabase-build`, `make terrabase-run` | TerraBase build and standalone run |
| `make dashboard-build`, `make dashboard-lint`, `make dashboard-check` | Dashboard build, asset/UI/format lint and security checks |
| `make dashboard-css`, `make dashboard-js` | Rebuild committed Dashboard CSS/JS assets |
| `make aspire-build` | Build the local Aspire AppHost |
| `make sdk-check`, `make sdk-<dotnet\|go\|js\|python\|rust>-check` | SDK build and type-checks |
| `make docs-build`, `make landing-build`, `make status-build` | Public site checks and builds |

## Local development with Aspire

`make dev` runs TerraBase, ts-auth and the Dashboard locally through the C# Aspire AppHost in [`src/TerraScale.Aspire`](src/TerraScale.Aspire), with the Aspire dashboard for logs, traces and health. Local development only; nothing touches production.

- Requires the .NET SDK pinned in [`global.json`](global.json). The Aspire CLI is a local tool in [`dotnet-tools.json`](dotnet-tools.json) (`make dev` restores it). Trust the local HTTPS certificate once with `dotnet aspire certs trust`.
- Every listener binds loopback: Dashboard on `127.0.0.1:8070`, TerraBase on `127.0.0.1:8080` (internal `8081`, metrics `9091`, Postgres `5432`) and ts-auth on `127.0.0.1:8090` (metrics `9092`).
- Set `TS_DEV_INSTANCE=1 make dev` to run an isolated second stack (`base + 100 * instance` for every port and `-<instance>` on the data directory).
- Every run is disposable: TerraBase starts from a clean `$TMPDIR/terrascale-aspire-terrabase` directory and all local secrets are freshly generated in memory.
- `.mcp.json` registers the Aspire MCP server (`dotnet aspire agent mcp`).
