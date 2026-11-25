# TerraBase

Read the root [AGENTS.md](../../AGENTS.md). Load the `csharp` skill before writing C#.

TerraBase is a multi-region SQL database. Clients use the Postgres wire protocol, REST and SQL WebSockets. `ts-platform` serves shapes and other platform APIs. Each region that holds a database can accept writes. TerraBase replicates commits to peers and supports bookmark reads and strong transactions.

## Structure and storage

- Use upstream SQLite3MultipleCiphers through `src/TerraBase.Storage/SqliteNative.cs`. `native/native-pin.env` pins the SQLite and SQLite3MultipleCiphers versions and the source archive hash.
- Ship one self-contained JIT binary, `terrabase`. No native AOT or trimming. Stay single-file compatible: no `Assembly.Location` or dependence on adjacent assembly files; preserve the single-file analyzer.
- Keep the database server in `src/TerraBase`, the storage implementation in `src/TerraBase.Storage` and peer RPC in `src/TerraScale.QuicRpc`. The internal API uses HTTP/2.
- Group related endpoints, records, services, options, storage, metrics and registration by feature under `Features/<Feature>/` (for example `Sql`, `PostgresHost`, `Rules`, `Backup` and `Regions`). Namespaces follow folders under `TerraBase`. Expose the smallest types each feature needs and keep unused internals private.
- Consume services, stores, engines and adapters through DI interfaces; implementations are internal and swappable without changing callers. `Program.cs` is the sole composition root, using top-level statements and `WebApplication.CreateSlimBuilder` (no `App` or `Startup` class). One shared process per node serves every listener.
- The storage layer owns SQLite database files and per-origin commit logs. Keep native handles and SQLite-specific types inside `src/TerraBase.Storage`.
- `StorageFormat.Id` in `src/TerraBase/Engine/StorageFormat.cs` names the persisted format. `.github/scripts/check-storage-format.sh` checks storage, engine and native build inputs. Apply `storage-format-unchanged` after a reviewer verifies that the bytes on disk stay the same. A format bump needs user approval and the `storage-format` label after the release rehearsal. TerraBase archives encrypted backups to OCI Object Storage.
- Derive identity/discovery automatically; add no operator settings. Validate derived inputs at startup and fail closed. Error messages name inputs without printing values.

## Listeners and resources

- One process binds client HTTP (default `8080`), internal HTTP/2 (`8081`), PostgreSQL (`5432`) and Prometheus metrics (`9091`) in `Hosting/Listeners.cs`. QuicRpc uses a separate UDP port (default `8443`). In standalone development the listeners bind loopback. In a cluster, client HTTP and PostgreSQL bind all interfaces and the edge terminates client TLS. The internal listener defaults to the node address. Metrics bind that address and loopback.
- Client listeners serve only endpoints with `Metadata(ListenerMetadata.Client)` and scoped authorization (e.g. `RequireAuthorization(ApiKeyScopes.SqlRead)`). Missing listener metadata means admin-only; wrong-listener requests return `404`.
- Regions, nodes and peers derive from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE`. Internal HTTP/2 requests use the `X-TerraBase-Peer-Credential` header with the configured peer secret or the caller identity required by the route. Replication and peer RPC use `TerraScale.QuicRpc`.
- APIs are asynchronous and cancellation-aware. No `.Result`, `.Wait()` or sync-over-async; no locks across `await`. Isolate blocking disk work and CPU-heavy maintenance from requests.
- Bound queues, caches, batches, buffers, streams and in-flight operations by count and bytes. Reserve memory before reading, decoding, queueing or materializing; release on errors and cancellation. Minimize hot-path copies and allocations with spans, memory, pools and pipelines; measure optimizations.
- Record Prometheus metrics through `System.Diagnostics.Metrics` on `TerraScale.TerraBase` (`Hosting/Telemetry.cs`); scrape `/metrics` on the metrics listener. Keep labels bounded; no tenant-controlled label values.
- Clients use the public `api.` host, with optional static override and pinned sessions. No hardcoded regional client endpoints.

## Build

- Build: `make terrabase-build`. CI's `terrabase` leg also checks whitespace (`make terrabase-fmt-check`).
