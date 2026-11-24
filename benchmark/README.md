# TerraScale E2E Benchmark Suite

End-to-end benchmarks driving real **TerraBase**, **ts-auth**, and **TerraScale.Dashboard** processes through the **C#**, **Go**, and **Rust** SDKs.

## Quick start

Run the full self-hosted benchmark suite from the repository root:

```bash
make bench
```

Or directly inside `benchmark/`:

```bash
make -C benchmark run
```

The root target builds TerraBase with the pinned SQLite3MultipleCiphers native library and builds ts-auth and Dashboard. The benchmark builds the Go orchestrator (`benchmark/bin/ts-bench`) and the C# and Rust SDK runners. In self-hosted mode, the orchestrator starts those three services on loopback with throwaway data, provisions the project and API key, runs the scenarios, and prints a Markdown comparison table. This harness covers SQL and service endpoint round trips. The full local platform stack, including ts-platform, is available through `make dev`.

Save a report to disk or tune iterations:

```bash
make -C benchmark run BENCH_ARGS="--iterations=100 --warmup=10 --batch-size=25 --out=bench/e2e-$(date -u +%Y-%m-%d).md"
```

## External target mode

Run the exact same C#, Go, and Rust benchmarks against an already-running TerraScale stack:

```bash
benchmark/bin/ts-bench \
  --terrabase-url https://api.terrascale.tech \
  --auth-url https://auth.terrascale.tech \
  --dashboard-url https://dash.terrascale.tech \
  --api-key "$TERRASCALE_API_KEY" \
  --project "$TERRASCALE_PROJECT_ID" \
  --runners=csharp,go,rust \
  --iterations=50 \
  --warmup=5 \
  --batch-size=25
```

## Scenarios

| Scenario | Runners | What it measures |
| --- | --- | --- |
| `auth_token_exchange` | C#, Go, Rust | Real `ts-auth` OAuth2 `client_credentials` token exchange (`POST /terrascale/oauth/token`). |
| `db_lifecycle` | C#, Go, Rust | Create database, run `CREATE TABLE`, and delete database via TerraBase management + SQL APIs. |
| `sql_rest_write` | C#, Go, Rust | Parameterized single-row `INSERT` over SQL REST (`POST .../sql/execute`). |
| `sql_rest_read` | C#, Go, Rust | Parameterized point `SELECT` over SQL REST. |
| `sql_rest_batch` | C#, Go, Rust | Multi-statement transactional `INSERT` batch (`--batch-size` statements per request). |
| `sql_ws_transaction` | C#, Go, Rust | Persistent WebSocket SQL session (`terrascale.sql.v2`), `INSERT` + `SELECT` inside `Transaction`. |
| `dashboard_roundtrip` | C#, Go, Rust | HTTP `GET /health/live` + `GET /.well-known/oauth-authorization-server` against `TerraScale.Dashboard`. |

## Library microbenchmarks

BenchmarkDotNet microbenchmarks for shared .NET libraries remain under `benchmark/dotnet/`:

- `benchmark/dotnet/TerraScale.Endpoints.Benchmarks`
- `benchmark/dotnet/TerraScale.Logging.Benchmarks`
