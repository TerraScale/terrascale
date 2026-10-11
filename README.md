# TerraScale

TerraScale is one monorepo for the platform's core code: **TerraBase** (leaderless, multi-region document database), **ts-auth** (hosted identity provider), the shared .NET libraries (endpoints, logging) and the benchmarks. The ts-dashboard and the SDKs (ts-go, ts-dotnet, ts-js, ts-rust, ts-cli) live in separate repositories.

Agent and contributor rules: [`AGENTS.md`](AGENTS.md).

## Layout

| Path | What |
|---|---|
| `src/` | Projects: libs (`TerraScale.Endpoints*`, `TerraScale.Logging`), auth (`TerraScale.Auth*`), terrabase (`TerraScale.TerraBase*`) |
| `tests/` | Integration suites (`*.Fast`, `*.Slow`), TestHarness projects, and the libs test projects |
| `benchmark/` | Go benchmark code and `benchmark/bench/` results; `benchmark/dotnet/` for .NET library benchmarks |
| `docs/` | `docs/terrabase/` and `docs/auth/`, each with a `README.md` |
| `scripts/` | `scripts/libs`, `scripts/auth`, `scripts/terrabase` |
| `.forgejo/workflows/` | CI workflows |

## Make targets

Run them from the repository root. Locally, prefix each with `nice -n 19`.

| Command | What it does |
|---|---|
| `make build` | Build the solution |
| `make test-fast` | Run the blocking integration suites |
| `make test-slow` | Run the non-blocking integration suites |
| `make check` | Everything required before pushing |
| `make libs-*`, `make auth-*`, `make terrabase-*`, `make benchmark-*` | Per-area targets |

## Docs

- TerraBase: [`docs/terrabase/README.md`](docs/terrabase/README.md)
- ts-auth: [`docs/auth/README.md`](docs/auth/README.md)
- Benchmarks: [`benchmark/README.md`](benchmark/README.md)
