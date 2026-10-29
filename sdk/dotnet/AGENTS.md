# .NET SDK

## Layout and checks

- Paths and commands below are relative to `sdk/dotnet/`; from the repository root use `make sdk-dotnet-test-fast`.
- Release workflow: `.github/workflows/sdk-dotnet-release.yml` at the repository root. Package identity remains `TerraScale.DotNet`.

- SDK: `src/TerraScale.DotNet`; TUnit tests: `tests/TerraScale.DotNet.Tests`; contract fixtures: `contracts/`.
- Keep related files and services together using feature-sliced design.
- Use the SDK pinned in `global.json`; nullable types, analyzers and warnings as errors stay enabled.
- Build: `dotnet build TerraScale.DotNet.slnx --configuration Release`.
- Fast: `bash scripts/test-fast.sh` (both supported frameworks, fully parallel).
- Release checks: `bash scripts/check-release.sh`.

## API rules

- Land the owning public REST, WebSocket or SSE contract before its SDK operation. Verify method/message, versioned endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. Never invent server behavior.
- APIs are async and cancellation-aware. Never use `.Result`, `.Wait()` or sync-over-async. Clients are thread-safe after construction; never dispose caller-owned `HttpClient`.
- Do not serialize independent caller work without a correctness reason. Bound batch/pagination concurrency and backpressure; stream with `IAsyncEnumerable<T>`.
- Retry only documented transient failures safe to repeat. Mutations need stable idempotency. Respect deadlines, cancellation, `Retry-After` and overload; never auto-retry validation, authentication, authorization or deterministic conflicts.
- Use discoverable TerraScale names and immutable records. Validate options at construction with actionable errors; document public APIs with XML docs.
- Fix first-party timeout, retry, concurrency and buffer limits in code. Preserve caller cancellation and security validation.
- No console output or process-wide mutable state. Only an explicitly owned reactive object may start and dispose hidden background work.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
