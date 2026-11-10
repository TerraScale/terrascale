# .NET SDK

## Layout and checks

- Paths and commands below are relative to `sdk/dotnet/`; from the repository root use `make sdk-dotnet-test-fast`.
- Release workflow: `.github/workflows/sdk-dotnet-release.yml` at the repository root. Package identity remains `TerraScale.DotNet`.

- SDK: `src/TerraScale.DotNet`; TUnit tests: `tests/TerraScale.DotNet.Tests`; contract fixtures: `contracts/`.
- EF Core provider: `src/TerraScale.EntityFrameworkCore` (package `TerraScale.EntityFrameworkCore`); its integration tests `tests/TerraScale.EntityFrameworkCore.Tests` start the TerraBase built from the same commit (`TERRABASE_BINARY`, set by `make sdk-dotnet-test-fast`).
- Keep related files and services together using feature-sliced design.
- Use the SDK pinned in `global.json`; nullable types, analyzers and warnings as errors stay enabled.
- Build: `dotnet build TerraScale.DotNet.slnx --configuration Release`.
- Fast: `bash scripts/test-fast.sh` (both supported frameworks, fully parallel).
- Release checks: `bash scripts/check-release.sh`.

## API rules

- Land the owning public REST, WebSocket or SSE contract before its SDK operation. Verify method/message, versioned endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. Never invent server behavior.
- Deploy order: a main push that touches `sdk/dotnet/**` releases ts-auth and ts-dashboard at once, while production TerraBase changes only through a manual `build-only` + `release-only` CI dispatch. SDK or EF Core provider code that calls a new TerraBase feature (a SQL function, route, field or error code it relies on) ships in a later PR than the server change and merges only after production TerraBase runs a build containing it. Open that PR as a draft that says so; the server PR keeps the SDK and provider on what production TerraBase already serves.
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
- Each area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Keep default parallelism.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. New features add at most one happy-path and one critical-failure test.
- Changes limited to `AGENTS.md`/`CLAUDE.md` skip area Fast tests in CI. Mixed commits keep the gates for the changed code.
