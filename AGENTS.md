# TerraScale .NET SDK Instructions

## Project Scope

- This repository is the official C#/.NET client SDK for TerraScale.
- Optimize for excellent developer experience: discoverable APIs, predictable names, strong typing, minimal setup, clear errors, and good IntelliSense documentation.
- The SDK must be safe for library consumers: no process-wide mutable state, no hidden background work unless explicitly configured, no console output, and no ambient service discovery.
- Keep the package focused. Do not add server components, dashboards, CLIs, workers, database engines, or internal service clients unless explicitly requested.

## Workspace Rules

- All C# project folders must start with the `TerraScale.` prefix.
- All `.csproj` file names must start with the `TerraScale.` prefix.
- All `.slnx` file names must start with the `TerraScale.` prefix.
- Do not add, rename, or keep C# project folders, `.csproj` files, or `.slnx` files that violate this naming rule.
- All C# projects must target `net11.0`.
- All C# projects must use the latest preview version of the C# language supported by the pinned .NET SDK.

## API Boundary

- The SDK must interact only with public repository and database HTTP endpoints implemented by `/workspaces/TerraScale/Internals/src/API/TerraScale.API`.
- Allowed endpoint families are the `TerraScale.API` database/repository surface under `/api/v1`, including:
  - `/databases`
  - `/databases/{database}`
  - `/databases/{database}/collections`
  - `/databases/{database}/statistics`
  - `/repositories`
  - `/repositories/{repository}`
  - `/repositories/{repository}/documents`
  - `/repositories/{repository}/documents/{documentId}`
  - `/repositories/{repository}/schema`
  - `/repositories/{repository}/schema/validate`
  - `/repositories/{repository}/tql`
- Do not call auth, account, user, organization, invitation, API-key-management, notification, search, audit, admin, health, internal RPC, MagicOnion, or service-to-service endpoints from this SDK.
- Authentication is caller-provided. Accept API keys or bearer tokens through SDK options or per-request context, attach them to allowed API calls, and never create, rotate, fetch, or manage credentials.
- Do not reference `TerraScale.API`, `TerraScale.InternalRpc`, `TerraScale.Authentication`, `TerraBase`, or other server/internal projects as project references. Treat TerraScale as an external HTTP API.
- Do not open TerraBase sessions, connect directly to storage, or bypass `TerraScale.API`.

## Entity Framework Core Integration

- TerraScale Entity Framework Core support must live in a separate `src/TerraScale.EntityFrameworkCore` project/package. Do not add EF Core package references to the base `src/TerraScale.DotNet` SDK project unless the user explicitly changes this boundary.
- The EF Core project must use `TerraScale.DotNet` as its HTTP transport and must only call the allowed public `/api/v1` database/repository endpoints. It must not reference TerraScale server projects, TerraBase client packages, or private service contracts.
- Treat the EF Core project as the TerraScale EF provider/source package: expose discoverable `DbContext`/DI integration, use EF Core model metadata for repository names and keys, keep all TerraScale I/O async and cancellation-aware, and test entity mapping plus document create/read/update/delete/query behavior.
- Keep EF Core integration tests in a separate `tests/TerraScale.EntityFrameworkCore.Tests` project so provider/source behavior is verified independently from the base SDK tests.

## SDK Design

- Use `HttpClient` as the transport and support `IHttpClientFactory`/dependency injection with `AddTerraScaleClient(...)`.
- Provide a simple default entry point such as `TerraScaleClient`, then organize focused sub-clients for databases, repositories, collections, entities, queries, schema, placement, and SQL.
- Make all network APIs asynchronous and suffix them with `Async`.
- Every async method must accept a `CancellationToken`, with `CancellationToken cancellationToken = default` only on public convenience overloads.
- Return strongly typed request and response models. Prefer immutable records for DTOs and options.
- Use `System.Text.Json` with source-generated metadata when practical. Do not use Newtonsoft.Json unless the API contract requires behavior that `System.Text.Json` cannot provide.
- Model API errors explicitly with a typed SDK exception or result type that preserves status code, error code, message, request ID, response body when safe, and retryability.
- Preserve response headers that matter to clients, including request IDs, route-affinity headers, pagination cursors, rate-limit headers, and retry-after values.
- Keep APIs composable. Expose raw response metadata when useful without forcing callers into low-level HTTP code.

## Async, Parallel, and Resilience

- The SDK must be async-first end to end. Do not block on tasks with `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, or sync-over-async wrappers.
- Support parallel caller usage. Client instances must be thread-safe after construction and safe to reuse as singletons.
- Do not serialize independent network work inside the SDK unless correctness requires it.
- Provide batch and pagination helpers that use bounded concurrency, backpressure, and cancellation-aware fan-out.
- Use `IAsyncEnumerable<T>` for streaming paginated reads, with clear methods for materializing pages or all results.
- Use modern .NET resilience primitives or `Microsoft.Extensions.Http.Resilience` for retries, timeouts, and transient failure handling. Retries must be opt-in or configured conservatively, respect idempotency, and honor `Retry-After`.
- Always dispose owned resources, but never dispose caller-owned `HttpClient` instances.

## Developer Experience

- Keep names short, consistent, and TerraScale-specific. Avoid leaking HTTP implementation details into the common path.
- Make the easy path obvious: configure base URL and token/API key, create a client, call a database or repository method.
- Provide XML documentation on public APIs.
- Include analyzers and nullable reference types. Treat warnings as errors.
- Validate options at construction time and fail with actionable messages.
- Make advanced configuration available without making basic usage verbose.
- Avoid surprising defaults. Network timeouts, retries, serialization options, and concurrency limits must be explicit in options or clearly documented defaults.

## Testing and Verification

- Do not create mocked, stubbed, faked, or local-handler tests. Tests must be integration or end-to-end tests that exercise real TerraScale API behavior.
- Maintain TUnit end-to-end coverage for every feature and function in this repository. Each feature/function must have at least one success-path E2E test and at least one failure-path E2E test; public E2E flows may cover internal helpers only when the exercised behavior is the same as the direct helper behavior.
- Integration and E2E tests must target the real `TerraScale.API` database/repository endpoints when credentials and endpoint configuration are available.
- If real API credentials or endpoints are unavailable in the default developer environment, keep the E2E tests environment-gated rather than replacing the API with a mock, stub, fake server, generated proxy, local handler, or in-memory stand-in.
- Build with all CPU cores, for example `dotnet build -m`.
