AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# Rules

- The project must follow a feature sliced design so it is well organized and similar or dependant files and services are kept together

# Libraries
- We should use TUnit for tests

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
- Keep timeouts, retry, concurrency, and buffer limits explicit.
- Provide XML documentation for public APIs.
- Enable nullable reference types, analyzers, and warnings as errors.
- Do not emit console output or use process-wide mutable state.
- Hidden background work is forbidden unless an explicitly owned reactive
  object starts and disposes it.
