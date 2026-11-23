# .NET SDK

Read the root [AGENTS.md](../../AGENTS.md). Load the `csharp` skill before writing C#.

## Layout and checks

- Paths and commands below are relative to `sdk/dotnet/`; from the repository root `make sdk-dotnet-check` restores and builds both frameworks.
- Release workflow: `.github/workflows/sdk-release.yml` (`SDK Release`) at the repository root, on `sdk/dotnet/v0.0.X` tags. Package identity: `TerraScale.DotNet`.
- SDK: `src/TerraScale.DotNet`; EF Core provider: `src/TerraScale.EntityFrameworkCore`; contract fixtures: `contracts/`.
- Keep related files and services together using feature-sliced design.
- Use the SDK pinned in `global.json`; nullable types, analyzers and warnings as errors stay enabled.
- Build: `dotnet build TerraScale.DotNet.slnx --configuration Release`. Release checks: `bash scripts/check-release.sh`.

## API rules

- Land the owning public REST, WebSocket or SSE contract before its SDK operation. Verify method/message, versioned endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. Never invent server behavior.
- **Deploy order:** a `main` push touching `sdk/dotnet/**` releases `ts-auth` and `ts-dashboard` at once, while production TerraBase changes only through a manual `build-only` + `release-only` CI dispatch. SDK or EF Core provider code calling a new TerraBase feature (SQL function, route, field or error code) must ship in a later PR and merge only after production TerraBase runs a build containing it. Open that PR as a draft noting the dependency.
- APIs are async and cancellation-aware. Never use `.Result`, `.Wait()` or sync-over-async. Clients are thread-safe after construction; never dispose caller-owned `HttpClient`.
- Do not serialize independent caller work without a correctness reason. Bound batch/pagination concurrency and backpressure; stream with `IAsyncEnumerable<T>`.
- Retry only documented transient failures safe to repeat. Mutations need stable idempotency. Respect deadlines, cancellation, `Retry-After` and overload; never auto-retry validation, authentication, authorization or deterministic conflicts.
- Use discoverable TerraScale names and immutable records. Validate options at construction with actionable errors; document public APIs with XML docs where `CS1591` is enforced.
- Fix first-party timeout, retry, concurrency and buffer limits in code. Preserve caller cancellation and security validation.
- No console output or process-wide mutable state. Only an explicitly owned reactive object may start and dispose hidden background work.
- `README.md` records implementation and qualification gaps; do not claim unshipped behavior.
