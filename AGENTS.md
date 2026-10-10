AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# Rules
- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.

- The project must follow a feature sliced design so it is well organized and similar or dependant files and services are kept together

# Libraries
- We should use TUnit for tests

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Public contract work

Land the owning public REST, WebSocket, or SSE contract before the SDK
operation. Do not invent a route, DTO, header, field, error, or server behavior.

Verify:

- method or protocol message;
- versioned path or endpoint;
- operation identity;
- parameters and requiredness;
- media types;
- field names, types, and nullability;
- security scheme and scope;
- success, error, and partial outcome;
- idempotency and retry;
- pagination or checkpoint semantics; and
- generated contract and route-manifest parity.

## Entity Framework Core

EF Core support stays in `src/TerraScale.EntityFrameworkCore`. It uses this
SDK and public Database API operations. It cannot reference TerraBase or server
projects.

Expose async, cancellation-aware `DbContext` integration. Map EF model
metadata to public database, environment, repository, key, schema, and query
contracts. Keep provider tests separate from base SDK tests.

## Async, retry, and resources

- APIs are asynchronous and cancellation-aware.
- Never block with `.Result`, `.Wait()`, or sync-over-async.
- SDK clients are thread-safe after construction.
- Do not serialize independent caller work without a correctness reason.
- Use bounded concurrency and backpressure for batches and pagination.
- Use `IAsyncEnumerable<T>` for streaming results.
- Never dispose a caller-owned `HttpClient`.
- Retry only documented transient failures that are safe to repeat.
- Mutations require stable idempotency before retry.
- Respect deadlines, cancellation, `Retry-After`, and overload.
- Do not retry validation, authentication, authorization, or deterministic
  conflict failures automatically.

## Developer experience

- Use discoverable TerraScale-specific names and immutable records.
- Validate options at construction with actionable errors.
- Fix first-party timeout, retry, concurrency, and buffer limits in code;
  preserve caller cancellation and security validation, without tuning knobs.
- Provide XML documentation for public APIs.
- Enable nullable reference types, analyzers, and warnings as errors.
- Do not emit console output or use process-wide mutable state.
- Hidden background work is forbidden unless an explicitly owned reactive
  object starts and disposes it.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; it discovers every region, infers its own region from where it runs and starts serving without a per-region enable step.
- OCI Vault automatically creates, delivers and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically; nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

The operating contract requires automatic public regional endpoint discovery. Customer credentials authorize access; production service credentials arrive from OCI Vault.

This section is the TS-795 operating contract, not evidence that every runtime
flow has shipped. See [README.md](README.md) for current SDK/qualification gaps.
Existing production switches are inventoried for their owning implementation
threads; documentation changes must not invent APIs or bypass authorization,
tenant scope, registered callbacks, payment consent, or safety guards.
