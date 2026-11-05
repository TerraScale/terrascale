# Benchmarks

`TerraScale.Endpoints.Benchmarks` maps the same five scenarios three ways in one app:

| Prefix | Implementation |
|--------|----------------|
| `/rdg` | Minimal API with the Request Delegate Generator (source generated, the AOT path) |
| `/rdf` | Minimal API through `RequestDelegateFactory` (reflection, the default JIT path) |
| `/ts`  | TerraScale endpoints |

The scenarios are `health` (GET, JSON response), `echo` (POST, JSON body to JSON
response), `send` (POST through `Send.OkAsync` vs `TypedResults.Ok`), `scoped`
(scoped DI dependency, string response) and `text` (plain string response).
Every variant must return the same status, content type and bytes; the handler
benchmark checks this before it measures anything.

Run builds and measurements through `ts-heavy-any` on an admitted dev host:

```sh
# Handler pipeline only: calls each endpoint's RequestDelegate with a fresh HttpContext and scope.
nice -n 19 dotnet run -c Release --project benchmark/dotnet/TerraScale.Endpoints.Benchmarks -- --filter '*Handler*'
# Rough numbers in seconds instead of minutes:
nice -n 19 dotnet run -c Release --project benchmark/dotnet/TerraScale.Endpoints.Benchmarks -- --filter '*Handler*' --quick
# Cost of each step TerraScale adds (DI activation, Task allocation, ...):
nice -n 19 dotnet run -c Release --project benchmark/dotnet/TerraScale.Endpoints.Benchmarks -- --filter '*Overhead*'
# End to end over Kestrel with oha: duration, connections, rounds.
nice -n 19 benchmark/dotnet/run-http.sh 10s 64 3
```

Results go to `benchmark/bench/`. Numbers from a shared or busy
machine are noisy; compare allocations (deterministic) first, and re-run timings
on an idle machine before drawing conclusions from small differences.

The SQL shared-process harness measures database admission and write latency.
`DatabaseEncryption` measures encrypted SQL writes, reads, checkpoints and reopen.
`TerraBase.LiveRestoreDrill` validates backups in private scratch storage through
a read-only transport. These diagnostics use internal engine types; their output
must describe the measured SQL behavior and preserve cleanup and recovery checks.
