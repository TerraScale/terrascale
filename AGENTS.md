# TerraBase Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Project

TerraBase is a **leaderless, multi-region document database** focused on fast and efficient synchronization and replication.

This repository is the **C#** implementation of TerraBase. The Rust implementation in `../terrabase-rust` is the reference for product goals, contracts, formats and decisions:

- `../terrabase-rust/GOAL.md` — product objective, priorities and acceptance gates.
- `../terrabase-rust/docs/` — architecture, storage format, distributed protocol, document/query/SQL contracts and decisions.

Port behavior from those documents, not line by line from Rust. When the C# implementation must diverge from a documented contract, record the decision in `docs/` and ask a human first.

When interacting with Forgejo, always use the `fj` CLI.

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Language and Stack

- This repository MUST use **C#** on **.NET 11** (`net11.0`, SDK pinned in `global.json`).
- Ship a **single self-contained binary** (`terrabase`): `PublishSingleFile` on the **JIT**. Do not enable native AOT or trimming. Code MUST stay single-file compatible (no `Assembly.Location` or reliance on files next to the assembly); the single-file analyzer runs in every build.
- Use `System.Text.Json` with source-generated `JsonSerializerContext` metadata; register every request/response type in `Hosting/TerraBaseJsonContext.cs`.
- **Newtonsoft.Json is banned.** Its APIs are listed in `BannedSymbols.txt` (enforced by the global `Microsoft.CodeAnalysis.BannedApiAnalyzers`, `RS0030` as an error), and `Directory.Build.targets` fails the build if any Newtonsoft package enters the graph, directly or transitively. Never remove these guards; choose a dependency that does not need Newtonsoft.
- Add newly banned APIs to `BannedSymbols.txt` with a reason that names the replacement.
- **Every** HTTP endpoint — client, administrative, health, examples — MUST use **`TerraScale.Endpoints`** from `../ts-libs` (one endpoint class per file, compile-time registration via `AddTerraScaleEndpoints()` / `MapTerraScaleEndpoints()` / `MapTerraScaleOpenApi()`). Never use raw minimal-API `Map*` calls or controllers for endpoints. The only exceptions are framework-provided endpoints with no TerraScale.Endpoints equivalent: the gRPC services (`MapGrpcService`), the Prometheus scrape and the OpenAPI/Scalar documents.
- All public HTTP APIs MUST have **OpenAPI** documentation rendered through **Scalar**.

## General

- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Prefer simple, maintainable code and established libraries over custom solutions or unnecessary abstractions.
- Follow repository conventions, keep changes focused, and avoid unrelated rewrites.
- Never invent APIs, package names, commands, issue IDs, or successful verification results.
- Preserve unrelated changes; never discard, overwrite, or commit another contributor’s work.
- Do not claim a guarantee (durability, consistency, compatibility, security) without the implementation and integration evidence that proves it.

## Dependencies

- Prefer mature, popular, actively maintained, and well-tested NuGet packages over custom implementations.
- Do not build functionality from scratch when a suitable established package exists unless there is a clear technical reason.
- Versions are managed centrally in `Directory.Packages.props`; never put a `Version` on a `PackageReference`.
- Use the latest stable, mutually compatible versions (prerelease only where .NET 11 requires it). Verify compatibility before upgrading.
- Commit the `packages.lock.json` files; CI restores in locked mode. Builds MUST remain reproducible.
- Package sources are fixed in `NuGet.config`: `TerraScale.*` packages restore only from the TerraScale Forgejo registry (`https://git.terrascale.tech/api/packages/TerraScale/nuget/index.json`); everything else only from nuget.org. Keep the `packageSourceMapping` in sync when adding a source.
- Sibling TerraScale libraries that are not consumed as packages are project references resolved through `TerraScaleRoot`.
- Shared MSBuild configuration lives in three root files; keep project files minimal:
  - `Directory.Build.props` — common properties for every project (framework, language, analyzers, lock files, `TerraScaleRoot`/`TsLibsRoot`).
  - `Directory.Build.targets` — rules that depend on project properties: `<UseTerraScaleEndpoints>true</UseTerraScaleEndpoints>` wires `TerraScale.Endpoints` and its source generator from `../ts-libs`; `<IsTestProject>true</IsTestProject>` adds TUnit, the server reference and the linked `TestHarness`.
  - `Directory.Packages.props` — every package version.

## Architecture

### Feature-sliced design

The project MUST follow a **feature-sliced design** so related and dependent files and services are kept together.

```
src/
  TerraScale.TerraBase/            # the server: one single-file binary
    Program.cs                     # top-level statements: parse options, compose services, middleware and endpoints, run
    Configuration/                 # validated process options (TERRABASE_* / --name=value)
    Hosting/                       # cross-cutting host concerns: listeners, telemetry, JSON context, version
    Engine/                        # storage engine interfaces (IStorageEngine, IDatabaseStore, IStorageTransaction)
      Sqlite/                      # the SQLite implementation; one folder per engine implementation
    Features/
      <Feature>/                   # one folder per vertical slice
  Tests/
    TerraScale.TerraBase.Fast/     # fast integration suite
    TerraScale.TerraBase.Slow/     # slow integration suite
    TestHarness/                   # shared test source, linked into both suites (not a project)
TerraScale.TerraBase.slnx
```

- **All projects live in `src/`**; test projects live in `src/Tests/`. All are listed in `TerraScale.TerraBase.slnx` at the repository root.
- A feature folder owns its endpoints, request/response records, services, options, storage access, metrics and `IServiceCollection` registration extension. Namespaces follow folders (`TerraScale.TerraBase.Features.<Feature>`).
- Features talk to each other through small, explicit public types. Do not reach into another feature’s internals; prefer `internal` for anything not used across slices.
- Truly shared primitives (validated IDs, typed errors, memory admission, versioned binary formats) go in a dedicated shared folder only once at least two features need them.
- **Program over interfaces.** Every service, store, engine and adapter is consumed through an interface (`IStorageEngine`, `IDocumentService`, `IApiKeyStore`, …) registered in DI; implementations are `internal`. Swapping an implementation (for example SQLite for Turso) means adding a new implementation of the same interface, never changing its callers.
- `Program.cs` is the only composition root and uses top-level statements with `WebApplication.CreateSlimBuilder`. Do not reintroduce an `App` class or `Startup`.
- Keep one server project by default. Extract a separate project only for a real boundary (native interop, generated gRPC contracts, a library consumed elsewhere), and ask a human first.

Expected slices, mapped from the Rust crates. Each slice starts with its public interface (and records) so the project structure is visible early; an interface without an implementation is marked "Not implemented yet" in its XML docs and is not registered in DI until its first real behavior lands:

| Slice | Responsibility | Rust reference |
| --- | --- | --- |
| `Health` | Liveness/readiness probes | `tb-server` |
| `Memory` | Process/tenant/worker memory budgets and admission | `tb-core` |
| `Engine/` (not under `Features/`) | Storage engine interfaces and implementations (SQLite), transactions, database open | `tb-storage`, `tb-engine-io` |
| `ObjectTier` | S3-compatible immutable objects, manifests, verified range reads | `tb-object` |
| `Documents` | Document catalog, HTTPS/JSON document API, queries, subscriptions | `tb-document`, `tb-server` |
| `Sql` | PostgreSQL-wire endpoint for the supported SQL subset | `tb-server` (`sql_*`) |
| `Replication` | Leaderless replication, journals, cursors, merge, gRPC transport | `tb-replication`, `tb-protocol` |
| `Membership` | Signed membership authority, epochs, member lifecycle | `tb-replication`, `tb-server` |
| `Catalog` | Offline catalog and schema administration | `tb-server` (`catalog_admin`) |
| `Backup` | Backup, restore and archival | `tb-object`, `tb-server` |
| `Indexing` | Deterministic initial index decisions, Von/JEV advisors, workload adaptation | `docs/auto-index.md` |
| `ApiKeys` | Scoped API keys and client authorization | `tb-server` (`client_auth`, `terrabase_keygen`) |

### Storage engine

- The storage engine is **SQLite** (`Microsoft.Data.Sqlite`), decided by a human on 2026-10-04 (`docs/decisions/0002-sqlite-storage-engine.md`): TerraBase is a database-as-a-service that stores JSON documents in SQLite, one file per database.
- All storage access goes through the `Engine/` interfaces. Code outside `Engine/<Implementation>/` never references SQLite types. Adding another engine (for example Turso) requires explicit human approval and is a new `Engine/<Name>/` implementation of the same interfaces.
- Every on-disk schema is versioned (`PRAGMA user_version`); refuse to open a newer version.
- Do not build a TerraBase-owned B-tree/LSM storage engine.

### Configuration

- Every setting is `TERRABASE_<NAME>` or the equivalent `--name=value` argument; an argument wins over the environment. Do not add configuration files.
- Validate all options at startup. Unknown or invalid settings stop the process with exit code `2`.
- Error messages name settings, never their values. Never log secrets, keys, tokens or credentials.

### Listeners

- The administrative listener (health, metrics) is **loopback-only** by construction (default port `9464`).
- Client-facing listeners MUST use TLS and scoped API keys. The client listener (`client-port`) serves only endpoints that declare `Metadata(ListenerMetadata.Client)` and `RequireAuthorization(<scope>)`.
- The peer listener (`peer-port`) serves only gRPC over mutual TLS.
- Endpoints without listener metadata belong to the administrative listener. Requests that arrive on the wrong listener get 404 (`Hosting/Listeners.cs`).

### Async, memory and resources

- APIs are asynchronous and cancellation-aware. Never block with `.Result`, `.Wait()`, or sync-over-async.
- Bound every queue, cache, batch, buffer, stream and in-flight operation by both item count and bytes.
- Reserve memory before reading, decoding, queueing or materializing data; release it on cancellation and error.
- Minimize allocations and copies on hot paths (`Span<T>`, `Memory<T>`, pooled buffers, `System.IO.Pipelines`); measure before optimizing.
- Do not hold locks across `await`. Isolate blocking disk work and CPU-heavy maintenance from request handling.
- Persisted formats MUST be explicitly versioned and independent of .NET type layout and platform endianness.

## Metrics

- TerraBase MUST expose **Prometheus-compatible metrics** for relevant operational and performance data.
- Record instruments on the `TerraScale.TerraBase` meter (`Hosting/Telemetry.cs`) using `System.Diagnostics.Metrics`; metrics are scraped at `/metrics` on the administrative listener.
- Keep label cardinality bounded; never use tenant-controlled or unbounded values as labels.

## Networking

- TerraBase-to-TerraBase communication MUST use **gRPC** with checked-in, versioned `.proto` contracts.
- Inter-node gRPC communication MUST use **LZ4 compression** to reduce bandwidth usage.
- Do not introduce alternative inter-node protocols without explicit human approval.

## Build

- Always use the repository `Makefile` when an equivalent Make target exists.
- Analyzers and code style run in the build with warnings as errors (`Directory.Build.props`, `.editorconfig`). Do not suppress warnings without a documented justification next to the suppression.
- `make check` (formatting, build, fast suite) MUST pass before pushing.

## Benchmarks

- ALL benchmarks MUST live in the sibling repository:

  `../ts-benchmark`

- Do NOT add benchmark code, benchmark datasets, benchmark reports, or benchmark results to this repository.
- Performance comparisons and historical benchmark results MUST remain in `../ts-benchmark`.

## Testing

> [!IMPORTANT]
> **Unit tests MUST NEVER be written. Only INTEGRATION tests are allowed, and every test MUST run in parallel.**

- Use **TUnit** for tests and **Bogus** for synthetic test data.
- Run suites with `make test-fast` / `make test-slow` (they use `dotnet run --project`, which is faster than `dotnet test` for TUnit).

### Parallel execution

- Every test MUST be safe to run in parallel with every other test, in both suites. TUnit runs tests in parallel by default; keep it that way.
- NEVER use `[NotInParallel]`, `[ParallelLimiter]`, sequential execution, or lower the parallelism to make a test pass. Fix the isolation problem instead.
- Each test owns its resources: its own server instance (`TerraBaseTestServer.StartAsync()`), ephemeral ports (`0`), temporary directories, buckets/prefixes, databases and synthetic data. Clean them up when the test ends.
- Never depend on test ordering, shared mutable static state, fixed ports, wall-clock sleeps, or another test’s data.

Maintain exactly two integration test suites:

- **Fast** (`src/Tests/TerraScale.TerraBase.Fast`)
  - Covers critical paths and common user workflows.
  - MUST remain fast enough to run routinely.
  - MUST run before every merge, deployment, release, and relevant CI action.
  - Any failure MUST block the operation.

- **Slow** (`src/Tests/TerraScale.TerraBase.Slow`)
  - Provides comprehensive integration and end-to-end coverage.
  - MUST run independently from the release-critical pipeline.
  - MUST NOT block merges, deployments, or releases.
  - Any failure MUST automatically create a Forgejo issue containing enough information to reproduce and investigate the failure.

### Testing Constraints

- Do NOT create unit tests.
- All tests MUST validate behavior through integration or end-to-end testing: drive the real application over its real listeners (`src/Tests/TestHarness/TerraBaseTestServer.cs`, which starts the top-level `Program` through `WebApplicationFactory<Program>.UseKestrel()` with its own data directory).
- Shared test utilities live in `src/Tests/TestHarness` and are linked as source; they MUST NOT become a third suite.
- New functionality MUST be covered by the appropriate integration test suite.
- Critical or commonly used functionality SHOULD be covered by the **Fast** suite.
- Broader, expensive, edge-case, stress, or exhaustive scenarios SHOULD be covered by the **Slow** suite.
- Never skip, disable, or weaken tests to bypass a failure.

## Issues and Git

- Track development work in **`ku.mariogk.com`**. Never invent a project prefix or issue ID; ask when none is supplied.
- Use `git` for repository operations, **`fj`** for Forgejo operations and Ku tooling for development issues. Check CLI help when syntax is uncertain.
- Prefer a dedicated Git worktree per task, branched from the latest `origin/main`, named `feat/`, `fix/`, `hotfix/`, or `refactor/` plus a descriptive name.
- Never implement task changes directly on or push directly to `main`.

## Pull Requests

- Target **`main`** with the title format **`{ISSUE_ID}: {title}`**.
- Include the Ku link, change summary, validation performed, and relevant compatibility, storage or security implications.
- Never claim a check passed unless it actually ran successfully. Distinguish required checks from non-blocking Slow results.
