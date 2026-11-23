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

This builds `bin/ts-bench`, the C# runner (`benchmark/csharp`), and the Rust runner (`benchmark/rust`), boots ephemeral loopback instances of `TerraBase`, `ts-auth`, and `TerraScale.Dashboard` in a temporary directory, provisions a real project and API key in `ts-auth`, executes every scenario sequentially across C#, Go, and Rust, tears down all processes cleanly, and prints a Markdown comparison table.

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
| `sql_ws_transaction` | C#, Go, Rust | Persistent WebSocket SQL session (`terrascale.sql.v1`), `INSERT` + `SELECT` inside `Transaction`. |
| `dashboard_roundtrip` | C#, Go, Rust | HTTP `GET /health/live` + `GET /.well-known/oauth-authorization-server` against `TerraScale.Dashboard`. |
| `efcore_roundtrip` | C# | `TerraScale.EntityFrameworkCore` insert (`SaveChangesAsync`) + read (`AsNoTracking().SingleAsync`). |

## Library microbenchmarks

BenchmarkDotNet microbenchmarks for shared .NET libraries remain under `benchmark/dotnet/`:

- `benchmark/dotnet/TerraScale.Endpoints.Benchmarks`
- `benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks`
- `benchmark/dotnet/TerraScale.Logging.Benchmarks`
