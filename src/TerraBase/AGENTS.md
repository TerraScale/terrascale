# TerraBase Agent Notes

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

Read [`AGENTS.md`](../../AGENTS.md) at the repository root first. Its rules apply here. This file adds the terrabase-only notes.

## Project

TerraBase is a **leaderless, multi-region document database** focused on fast and efficient synchronization and replication.

This area (`src/TerraBase`) is the **C#** implementation. The Rust implementation in `../terrabase-rust` (relative to the repository root) is the reference for product goals, contracts, formats and decisions: `../terrabase-rust/GOAL.md` (objective, priorities, acceptance gates) and `../terrabase-rust/docs/` (architecture, storage format, distributed protocol, document/query/SQL contracts, decisions).

Port behavior from those documents, not line by line from Rust. When the C# implementation must diverge from a documented contract, record the decision in `docs/terrabase/` and ask a human first.

## Language and stack

- Ship a **single self-contained binary** (`terrabase`): `PublishSingleFile` on the **JIT**. No native AOT or trimming. Code MUST stay single-file compatible: no `Assembly.Location` or reliance on files next to the assembly. The single-file analyzer runs in every build.
- Newtonsoft is banned here too. See the root .NET rules; the guard list is `BannedSymbols.txt`.

## Architecture

### Feature-sliced design

The project MUST follow a **feature-sliced design** so related and dependent files and services stay together.

- One server project, `src/TerraBase`. Analyzers live in `src/TerraBase.Analyzers` and `src/TerraBase.Analyzers.BuildCheck`. Extract a separate project only for a real boundary (native interop, generated gRPC contracts, a library consumed elsewhere), and ask a human first.
- A feature folder (`src/TerraBase/Features/<Feature>/`) owns its endpoints, request/response records, services, options, storage access, metrics and `IServiceCollection` registration extension. Namespaces follow folders (`TerraBase.Features.<Feature>`; the C# root namespace is `TerraBase`).
- Features talk through small, explicit public types. Do not reach into another feature's internals; prefer `internal` for anything not used across slices.
- Shared primitives (validated IDs, typed errors, memory admission, versioned binary formats) go in a shared folder only once at least two features need them.
- **Program over interfaces.** Every service, store, engine and adapter is consumed through a DI-registered interface (`IStorageEngine`, `IDocumentService`, `IApiKeyStore`, ...). Implementations are `internal`. Swapping one (for example SQLite for Turso) means a new implementation of the same interface, never changes to its callers.
- `src/TerraBase/Program.cs` is the only composition root: top-level statements with `WebApplication.CreateSlimBuilder`. Do not reintroduce an `App` class or `Startup`.

### Storage engine

- The storage engine is **SQLite** (`Microsoft.Data.Sqlite`), decided by a human on 2026-10-04 (`docs/terrabase/decisions/0002-sqlite-storage-engine.md`). TerraBase is a database-as-a-service that stores JSON documents in SQLite, one file per database.
- All storage access goes through the `Engine/` interfaces. Code outside `src/TerraBase/Engine/<Implementation>/` never references SQLite types. Another engine (for example Turso) requires explicit human approval and is a new `Engine/<Name>/` implementation of the same interfaces.
- Every on-disk schema is versioned (`PRAGMA user_version`). Refuse to open a newer version.
- Do not build a TerraBase-owned B-tree/LSM storage engine.
- When TerraBase needs external persistence (a log, snapshot or backup), it uses Tigris, the only object store. Never add a separate store.

### Configuration

- Production values have one correct setting in code. Derive identity and discovery automatically (`FLY_REGION`, `FLY_MACHINE_ID`, `FLY_APP_NAME`, `.internal` DNS) and obtain secrets from fly secrets. Do not add operator settings.
- Validate derived inputs and credentials at startup. Reject invalid inputs and fail closed.
- Error messages name settings, never their values. Never log secrets, keys, tokens or credentials.

### Listeners

- The administrative listener (health, metrics) is **loopback-only** by construction (default port `9464`).
- Client-facing listeners MUST use TLS and scoped API keys. The client listener (`client-port`) serves only endpoints that declare `Metadata(ListenerMetadata.Client)` and `RequireAuthorization(<scope>)`.
- The peer listener (`peer-port`) serves only gRPC over mutual TLS.
- Endpoints without listener metadata belong to the administrative listener. Requests that arrive on the wrong listener get 404 (`src/TerraBase/Hosting/Listeners.cs`).

### Async, memory and resources

- APIs are asynchronous and cancellation-aware. Never block with `.Result`, `.Wait()`, or sync-over-async. Do not hold locks across `await`. Isolate blocking disk work and CPU-heavy maintenance from request handling.
- Bound every queue, cache, batch, buffer, stream and in-flight operation by both item count and bytes.
- Reserve memory before reading, decoding, queueing or materializing data. Release it on cancellation and error.
- Minimize allocations and copies on hot paths (`Span<T>`, `Memory<T>`, pooled buffers, `System.IO.Pipelines`). Measure before optimizing.
- Persisted formats MUST be explicitly versioned and independent of .NET type layout and platform endianness.

## Metrics

- TerraBase MUST expose **Prometheus-compatible metrics** for relevant operational and performance data. Record instruments on the `TerraScale.TerraBase` meter (`src/TerraBase/Hosting/Telemetry.cs`) with `System.Diagnostics.Metrics`. Metrics are scraped at `/metrics` on the administrative listener.
- Keep label cardinality bounded. Never use tenant-controlled or unbounded values as labels.

## Networking

- Clients connect to the public `api.` host; Fly Proxy routes to the nearest region. Allow an explicit static override and pin sessions. Never hardcode regional endpoints in client code. Follow `docs/terrabase/region-identity.md`.
- TerraBase-to-TerraBase communication MUST use **gRPC** with checked-in, versioned `.proto` contracts.
- Inter-node gRPC MUST use **LZ4 compression** to reduce bandwidth usage.
- Do not introduce alternative inter-node protocols without explicit human approval.

## Build

- Build with the root `Makefile` (`terrabase-*` targets). Run `make check` before pushing.

## Testing

- Fast suite: `tests/TerraBase.Fast`. Slow suite: `tests/TerraBase.Slow`.
- All tests MUST validate behavior through integration or end-to-end testing. Drive the real application over its real listeners: `tests/TerraBase.TestHarness/TerraBaseTestServer.cs` starts the top-level `Program` through `WebApplicationFactory<Program>.UseKestrel()` with its own data directory.
- Never skip, disable, or weaken tests to bypass a failure.
