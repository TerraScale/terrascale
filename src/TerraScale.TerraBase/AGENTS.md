# TerraBase Agent Notes

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

Read [`AGENTS.md`](../../AGENTS.md) at the repository root first. Its rules apply here. This file adds the terrabase-only notes.

## Project

TerraBase is a **leaderless, multi-region document database** focused on fast and efficient synchronization and replication.

This area (`src/TerraScale.TerraBase`) is the **C#** implementation of TerraBase. The Rust implementation in `../terrabase-rust` (relative to the repository root) is the reference for product goals, contracts, formats and decisions:

- `../terrabase-rust/GOAL.md`: product objective, priorities and acceptance gates.
- `../terrabase-rust/docs/`: architecture, storage format, distributed protocol, document/query/SQL contracts and decisions.

Port behavior from those documents, not line by line from Rust. When the C# implementation must diverge from a documented contract, record the decision in `docs/terrabase/` and ask a human first.

## Language and stack

- Ship a **single self-contained binary** (`terrabase`): `PublishSingleFile` on the **JIT**. Do not enable native AOT or trimming. Code MUST stay single-file compatible (no `Assembly.Location` or reliance on files next to the assembly). The single-file analyzer runs in every build.
- Newtonsoft is banned in this area too; see the .NET rules in the root `AGENTS.md`. Its guard list is `BannedSymbols.txt` at the repository root.

## Architecture

### Feature-sliced design

The project MUST follow a **feature-sliced design** so related and dependent files and services are kept together.

- The server is one project, `src/TerraScale.TerraBase`. Analyzers live in `src/TerraScale.TerraBase.Analyzers` and `src/TerraScale.TerraBase.Analyzers.BuildCheck`. Keep one server project by default. Extract a separate project only for a real boundary (native interop, generated gRPC contracts, a library consumed elsewhere), and ask a human first.
- A feature folder (`src/TerraScale.TerraBase/Features/<Feature>/`) owns its endpoints, request/response records, services, options, storage access, metrics and `IServiceCollection` registration extension. Namespaces follow folders (`TerraBase.Features.<Feature>`, because the C# root namespace is `TerraBase`).
- Features talk to each other through small, explicit public types. Do not reach into another feature's internals; prefer `internal` for anything not used across slices.
- Truly shared primitives (validated IDs, typed errors, memory admission, versioned binary formats) go in a dedicated shared folder only once at least two features need them.
- **Program over interfaces.** Every service, store, engine and adapter is consumed through an interface (`IStorageEngine`, `IDocumentService`, `IApiKeyStore`, ...) registered in DI. Implementations are `internal`. Swapping an implementation (for example SQLite for Turso) means adding a new implementation of the same interface, never changing its callers.
- `src/TerraScale.TerraBase/Program.cs` is the only composition root and uses top-level statements with `WebApplication.CreateSlimBuilder`. Do not reintroduce an `App` class or `Startup`.

### Storage engine

- The storage engine is **SQLite** (`Microsoft.Data.Sqlite`), decided by a human on 2026-10-04 (`docs/terrabase/decisions/0002-sqlite-storage-engine.md`). TerraBase is a database-as-a-service that stores JSON documents in SQLite, one file per database.
- All storage access goes through the `Engine/` interfaces. Code outside `src/TerraScale.TerraBase/Engine/<Implementation>/` never references SQLite types. Adding another engine (for example Turso) requires explicit human approval and is a new `Engine/<Name>/` implementation of the same interfaces.
- Every on-disk schema is versioned (`PRAGMA user_version`). Refuse to open a newer version.
- Do not build a TerraBase-owned B-tree/LSM storage engine.
- When TerraBase needs external persistence (a log, durable queue or snapshot), it uses the same NATS (JetStream) as ts-orchestrator. Never add a separate store.

### Configuration

- Production values have one correct setting in code. Derive identity and discovery automatically and obtain secrets through OCI Vault. Legacy option removal belongs to TS-773; do not add operator settings.
- Validate derived inputs and credentials at startup. Reject invalid inputs and fail closed.
- Error messages name settings, never their values. Never log secrets, keys, tokens or credentials.

### Listeners

- The administrative listener (health, metrics) is **loopback-only** by construction (default port `9464`).
- Client-facing listeners MUST use TLS and scoped API keys. The client listener (`client-port`) serves only endpoints that declare `Metadata(ListenerMetadata.Client)` and `RequireAuthorization(<scope>)`.
- The peer listener (`peer-port`) serves only gRPC over mutual TLS.
- Endpoints without listener metadata belong to the administrative listener. Requests that arrive on the wrong listener get 404 (`src/TerraScale.TerraBase/Hosting/Listeners.cs`).

### Async, memory and resources

- APIs are asynchronous and cancellation-aware. Never block with `.Result`, `.Wait()`, or sync-over-async.
- Bound every queue, cache, batch, buffer, stream and in-flight operation by both item count and bytes.
- Reserve memory before reading, decoding, queueing or materializing data. Release it on cancellation and error.
- Minimize allocations and copies on hot paths (`Span<T>`, `Memory<T>`, pooled buffers, `System.IO.Pipelines`). Measure before optimizing.
- Do not hold locks across `await`. Isolate blocking disk work and CPU-heavy maintenance from request handling.
- Persisted formats MUST be explicitly versioned and independent of .NET type layout and platform endianness.

## Metrics

- TerraBase MUST expose **Prometheus-compatible metrics** for relevant operational and performance data.
- Record instruments on the `TerraScale.TerraBase` meter (`src/TerraScale.TerraBase/Hosting/Telemetry.cs`) using `System.Diagnostics.Metrics`. Metrics are scraped at `/metrics` on the administrative listener.
- Keep label cardinality bounded. Never use tenant-controlled or unbounded values as labels.

## Networking

- Resolve client regions and endpoints through ts-atlas. Allow an explicit static override and pin sessions. Never hardcode regional endpoints in client code. Follow the landed Atlas pin contract and `docs/terrabase/region-identity.md`.
- TerraBase-to-TerraBase communication MUST use **gRPC** with checked-in, versioned `.proto` contracts.
- Inter-node gRPC communication MUST use **LZ4 compression** to reduce bandwidth usage.
- Do not introduce alternative inter-node protocols without explicit human approval.

## Build

- `TerraScale.TerraBase` builds with the root `Makefile` (`terrabase-*` targets). Run `make check` before pushing.

## Testing

- The Fast suite is `tests/TerraScale.TerraBase.Fast`; the Slow suite is `tests/TerraScale.TerraBase.Slow`.
- All tests MUST validate behavior through integration or end-to-end testing. Drive the real application over its real listeners: `tests/TerraScale.TerraBase.TestHarness/TerraBaseTestServer.cs` starts the top-level `Program` through `WebApplicationFactory<Program>.UseKestrel()` with its own data directory.
- Never skip, disable, or weaken tests to bypass a failure.
