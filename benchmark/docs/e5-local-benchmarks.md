# Local benchmarks after E5

Leaderless replication, repair, refill, remote acknowledgement and cross-region convergence benchmarks have been removed with their implementation. Their reports describe the deleted engine and are removed too.

The local Shapes, PITR, text-search, vector and API benchmarks remain. Their shared HTTP client, resource sampler, canonical JSON and private result-file helpers live in `internal/benchclient`.

`dotnet/LocalBenchmarkHost.cs` compiles into the E2E executable and starts one real TerraBase host in `gru`. The host uses an isolated test catalog and database. It writes a private targets file named by `TERRABASE_BENCH_TARGETS` and stops when the caller removes that file. The caller must place it in a private temporary directory because it contains a synthetic access token. Local benchmark helpers never configure replication peers or wait for remote acknowledgements.

The Shapes command and the local text-search/vector scripts use this host. A real private `ts-*` test bucket can supply backup and shape objects through the existing bucket environment variables; otherwise the test binary uses its isolated S3 boundary fixture. Production credentials and real customer data must not be used.

The vs-engine benchmark retains local raw-engine, engine-service and HTTP lanes. Its three-region lanes have been removed. The cold-open benchmark measures local database creation and first metadata reads.

The feed benchmark retains local feed-prefix and reader measurements. Anti-entropy digest and pull modes have been removed. PITR clock sampling uses the health endpoint; no backup command depends on a replication status endpoint.

`make terrabase-e2e-local` runs the three retained local-only integration scenarios: two backup qualification scenarios and the local shape cache scenario. Its exact-count and pass-only result checks remain required in the native and container CI lanes.

No new benchmark measurements are supplied by this refactor. Future results must use three repeats and describe the local process and test-boundary costs they include.
