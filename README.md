# TerraScale

TerraScale is one monorepo for the platform's core code: **TerraBase** (leaderless, multi-region document database), **ts-auth** (hosted identity provider), the shared .NET libraries (endpoints, logging) and the benchmarks. The ts-dashboard and the SDKs (ts-go, ts-dotnet, ts-js, ts-rust, ts-cli) live in separate repositories.

Agent and contributor rules: [`AGENTS.md`](AGENTS.md).

## Layout

| Path | What |
|---|---|
| `src/` | Projects: libs (`TerraScale.Endpoints*`, `TerraScale.Logging`), auth (`TerraScale.Authentication*`), terrabase (`TerraBase*`) |
| `tests/` | Integration suites (`*.Fast`, `*.Slow`), TestHarness projects, and the libs test projects |
| `benchmark/` | Go benchmark code and `benchmark/bench/` results; `benchmark/dotnet/` for .NET library benchmarks |
| `docs/` | `docs/terrabase/` and `docs/auth/`, each with a `README.md` |
| `scripts/` | `scripts/libs`, `scripts/auth`, `scripts/terrabase` |
| `.github/workflows/` | CI (GitHub Actions, GitHub-hosted runners); `Dockerfile` holds its build and test stages |

## Make targets

Run them from the repository root. Locally, prefix each with `nice -n 19`.

| Command | What it does |
|---|---|
| `make build` | Build the solution |
| `make test-fast` | Run the blocking integration suites |
| `make test-slow` | Run the non-blocking integration suites |
| `make check` | Everything required before pushing |
| `make dev` | Run TerraBase and ts-auth locally under Aspire (see below) |
| `make libs-*`, `make auth-*`, `make terrabase-*`, `make benchmark-*` | Per-area targets |

## Local development with Aspire

`make dev` runs TerraBase and ts-auth on this machine through the [Aspire](https://aspire.dev) TypeScript AppHost in
`aspire/apphost.mts`, with the Aspire dashboard for their logs, traces and health. It is local development only; nothing
here deploys or touches Fly.io.

- Needs Node.js 20.19+ and ts-dotnet at the revision pinned in `.github/workflows/ci.yml` (`TS_DOTNET_REV`). The Aspire
  CLI is a local dotnet tool (`dotnet-tools.json`), run as `dotnet aspire`.
- From a worktree, or when `../ts-dotnet` is on another revision, clone the pinned revision on its own instead of moving
  that checkout, and point `TS_DOTNET_DIR` at it:

  ```sh
  git clone --no-checkout ../ts-dotnet /tmp/ts-dotnet-pinned   # or the GitHub repository
  git -C /tmp/ts-dotnet-pinned checkout --detach <TS_DOTNET_REV>
  nice -n 19 make dev TS_DOTNET_DIR=/tmp/ts-dotnet-pinned
  ```

  Direct `dotnet` and `make` commands take the same checkout as `TsDotnetRoot` (`-p:TsDotnetRoot=<dir>` or the
  environment variable).
- Every listener stays on loopback: TerraBase binds `127.0.0.1` in Development off Fly (8080 public, 8081 internal,
  9091 metrics) and ts-auth listens on `127.0.0.1:8090` (metrics 9092). Off Fly, ts-auth stores in the TerraBase at
  `localhost:8081`.
- Each run is disposable: a new temporary TerraBase data directory and a new ts-auth master key held only in memory.
- ts-auth answers `/readyz` 503 ("no signing keys") until the platform issuer has a signing key. The `auth-signing-key`
  resource runs `ts-auth provision-signing-key` against that run's local TerraBase only; it never starts by itself
  (start it from the dashboard). See [docs/auth/signing-keys.md](docs/auth/signing-keys.md).
- Agents: `.mcp.json` registers the Aspire MCP server (`dotnet aspire agent mcp`, stdio) for Claude Code; it asks for
  approval the first time.

## Docs

- TerraBase: [`docs/terrabase/README.md`](docs/terrabase/README.md)
- ts-auth: [`docs/auth/README.md`](docs/auth/README.md)
- Benchmarks: [`benchmark/README.md`](benchmark/README.md)
