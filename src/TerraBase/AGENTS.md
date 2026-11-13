# TerraBase

Read the root [AGENTS.md](../../AGENTS.md), including its shared shipping rules.

TerraBase is a SQL-only, multi-region database. Clients query with SQL over the Postgres wire protocol, REST and WebSocket, and read shapes over HTTP GET for CDN-cached reads. Writes commit in the closest region — safe statements locally, unsafe ones forwarded to the home region — and regions sync from home. Turso runs under the hood and is never named publicly. Current contracts and decisions live in `docs/terrabase/`.

## Structure and storage

- Never fork the Turso database: use upstream tursodatabase/turso at a pinned commit, tarball verified by SHA-256 at build time (benchmark/turso-sync-host/turso-pin.env). Only the Turso client may be forked (today: the vendored driver in src/vendor/Turso.Data.Sqlite), and only when upstream can't do what we need.
- Ship one self-contained JIT binary, `terrabase`. No native AOT or trimming. Stay single-file compatible: no `Assembly.Location` or dependence on adjacent assembly files; preserve the single-file analyzer.
- Keep one server project, `src/TerraBase`. Analyzers live in `src/TerraBase.Analyzers`. Separate projects require a real boundary, such as native interop, generated gRPC contracts or an externally consumed library.
- Group related endpoints, records, services, options, storage, metrics and registration by feature under `Features/<Feature>/` (Sql, Postgres, Shapes, Management, Backup, FileStorage, UserAuth, Regions, Replicas, Sync, and the rest). Namespaces follow folders under `TerraBase`. Features communicate through small public types; keep unused internals internal. Share primitives only when two features need them.
- Consume services, stores, engines and adapters through DI interfaces; implementations are internal. Swapping an implementation must not change callers. `Program.cs` is the sole composition root, using top-level statements and `WebApplication.CreateSlimBuilder`; no App or Startup class. One shared process per node serves every listener.
- The storage engine is Turso (decision `docs/terrabase/decisions/0014-turso-storage-engine-naming.md`), with one database file per database. All storage goes through `Engine/` interfaces; engine-specific types stay inside their implementation directory. Another engine uses the same interfaces. Do not build a TerraBase B-tree or LSM engine.
- The write router (`Features/Sql/Routing/`) decides per statement whether it commits locally or is forwarded to the home region; regions then pull updates from home (`Features/Sync`, `Features/Replicas`).
- Version on-disk schemas (`PRAGMA user_version`) and refuse newer versions. Persisted formats are explicitly versioned and independent of .NET layouts and platform endianness. External logs, snapshots and backups use Tigris.
- Derive identity/discovery automatically; add no operator settings. Validate derived inputs at startup and fail closed. Error messages name inputs without printing values.

## Listeners and resources

- One process binds four listeners (`Hosting/Listeners.cs`): client HTTP/REST (default 8080), internal peer HTTP/2 (8081), Postgres wire (5432) and Prometheus metrics (9091). In development without a peers file every listener binds loopback; in a cluster the internal listener binds the node's own address, metrics binds that address and loopback, and client HTTP and Postgres bind all interfaces. Client traffic arrives TLS-terminated at the edge; non-edge plaintext client requests are refused with `tls_required`.
- Client listeners serve only endpoints with `Metadata(ListenerMetadata.Client)` and scoped authorization (for example `RequireAuthorization(ApiKeyScopes.SqlRead)`). Missing listener metadata means admin-only; wrong-listener requests return 404.
- Regions, nodes and peers derive from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE`. Inter-region calls authenticate with the `TERRABASE_PEER_SECRET` credential header over the internal listener; the clock and backup-key-revocation services use versioned gRPC proto contracts. Keep alternate protocols out.
- APIs are asynchronous and cancellation-aware. No `.Result`, `.Wait()` or sync-over-async; no locks across await. Isolate blocking disk work and CPU-heavy maintenance from requests.
- Bound queues, caches, batches, buffers, streams and in-flight operations by count and bytes. Reserve memory before reading, decoding, queueing or materializing; release on errors and cancellation. Minimize hot-path copies and allocations with spans, memory, pools and pipelines. Measure optimizations.
- Record Prometheus metrics through `System.Diagnostics.Metrics` on `TerraScale.TerraBase` (`Hosting/Telemetry.cs`); scrape `/metrics` on the metrics listener. Keep labels bounded; no tenant-controlled label values.
- Clients use the public `api.` host, with optional static override and pinned sessions. No hardcoded regional client endpoints; follow `docs/terrabase/region-identity.md`.

## Build


- Build: `make terrabase-build`. CI's `terrabase` leg also checks whitespace (`make fmt-check SOLUTION=terrabase.slnf`).
- The journey in `e2e/` is the only test: it drives the TerraBase built from the commit under test. A behaviour that matters becomes a journey step (`e2e/AGENTS.md`).
