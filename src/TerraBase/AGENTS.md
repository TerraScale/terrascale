# TerraBase

Read the root [AGENTS.md](../../AGENTS.md), including its shared shipping rules.

TerraBase is a leaderless, multi-region document database. Current contracts and decisions live in `docs/terrabase/`; do not rely on a removed Rust checkout.

## Structure and storage

- Never fork the Turso database: use upstream tursodatabase/turso at a pinned commit, tarball verified by SHA-256 at build time (benchmark/turso-sync-host/turso-pin.env). Only the Turso client may be forked (today: the vendored driver in src/vendor/Turso.Data.Sqlite), and only when upstream can't do what we need.
- Ship one self-contained JIT binary, `terrabase`. No native AOT or trimming. Stay single-file compatible: no `Assembly.Location` or dependence on adjacent assembly files; preserve the single-file analyzer.
- Keep one server project, `src/TerraBase`. Analyzers live in `src/TerraBase.Analyzers` and its BuildCheck project. Separate projects require a real boundary, such as native interop, generated gRPC contracts or an externally consumed library.
- Group related endpoints, records, services, options, storage, metrics and registration by feature under `Features/<Feature>/`. Namespaces follow folders under `TerraBase`. Features communicate through small public types; keep unused internals internal. Share primitives only when two features need them.
- Consume services, stores, engines and adapters through DI interfaces; implementations are internal. Swapping an implementation must not change callers. `Program.cs` is the sole composition root, using top-level statements and `WebApplication.CreateSlimBuilder`; no App or Startup class.
- The current engine is Turso (`Engine/Turso`, decision `docs/terrabase/decisions/0014-turso-storage-engine-naming.md`), with one database file per database. All storage goes through `Engine/` interfaces; engine-specific types stay inside their implementation directory. Another engine uses the same interfaces. Do not build a TerraBase B-tree or LSM engine.
- Version on-disk schemas (`PRAGMA user_version`) and refuse newer versions. Persisted formats are explicitly versioned and independent of .NET layouts and platform endianness. External logs, snapshots and backups use Tigris.
- Derive identity/discovery automatically; add no operator settings. Validate derived inputs at startup and fail closed. Error messages name inputs without printing values.

## Listeners and resources

- Admin health/metrics are loopback-only (default 9464). Client listeners require TLS and scoped authorization, and serve only endpoints with `Metadata(ListenerMetadata.Client)` and `RequireAuthorization(<scope>)`. Peer listeners are gRPC over mutual TLS. Missing listener metadata means admin-only; wrong-listener requests return 404 (`Hosting/Listeners.cs`).
- APIs are asynchronous and cancellation-aware. No `.Result`, `.Wait()` or sync-over-async; no locks across await. Isolate blocking disk work and CPU-heavy maintenance from requests.
- Bound queues, caches, batches, buffers, streams and in-flight operations by count and bytes. Reserve memory before reading, decoding, queueing or materializing; release on errors and cancellation. Minimize hot-path copies and allocations with spans, memory, pools and pipelines. Measure optimizations.
- Record Prometheus metrics through `System.Diagnostics.Metrics` on `TerraScale.TerraBase` (`Hosting/Telemetry.cs`); scrape `/metrics` on admin. Keep labels bounded; no tenant-controlled label values.
- Clients use the public `api.` host, with optional static override and pinned sessions. No hardcoded regional client endpoints; follow `docs/terrabase/region-identity.md`.
- Peer traffic uses checked-in, versioned gRPC proto contracts and LZ4 compression. Keep alternate protocols out.

## Build and tests

- Build: `make terrabase-build`. Fast pre-push: `make terrabase-test-fast`. Slow: `make terrabase-test-slow`. Additional checks: `make terrabase-analyzer-check`, `make terrabase-check`.
- Fast/Slow suites live in `tests/TerraBase.Fast` and `tests/TerraBase.Slow`. Drive the actual app over real listeners. `tests/TerraBase.TestHarness/TerraBaseTestServer.cs` starts top-level Program through `WebApplicationFactory<Program>.UseKestrel()` with an owned data directory.
