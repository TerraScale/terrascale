AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# Rules
- No AI attribution in commits or PRs: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]`, no similar notices.
- Use feature-sliced design: keep similar or dependent files and services together.

# Libraries
- Use TUnit for tests.

## Local CPU Priority
- Prefix local builds, tests, lints, benchmarks, dev servers, and watchers with `nice -n 19` (for example `nice -n 19 make test-fast`).
- CI (`CI=true`) runs at full priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts, or Containerfiles.

## Public contract work
Land the owning public REST, WebSocket, or SSE contract before the SDK operation. Never invent a route, DTO, header, field, error, or server behavior. Verify: method or protocol message; versioned path or endpoint; operation identity; parameters and requiredness; media types; field names, types, and nullability; security scheme and scope; success, error, and partial outcomes; idempotency and retry; pagination or checkpoint semantics; generated contract and route-manifest parity.

## Entity Framework Core
Keep EF Core support in `src/TerraScale.EntityFrameworkCore`. It uses this SDK and public Database API operations and cannot reference TerraBase or server projects. Expose async, cancellation-aware `DbContext` integration. Map EF model metadata to public database, environment, repository, key, schema, and query contracts. Keep provider tests apart from base SDK tests.

## Async, retry, and resources
- APIs are asynchronous and cancellation-aware. Never block (`.Result`, `.Wait()`, sync-over-async).
- SDK clients are thread-safe after construction. Never dispose a caller-owned `HttpClient`.
- Don't serialize independent caller work without a correctness reason. Bound concurrency and backpressure for batches and pagination. Stream results with `IAsyncEnumerable<T>`.
- Retry only documented transient failures that are safe to repeat; mutations need stable idempotency first. Respect deadlines, cancellation, `Retry-After`, and overload. Never auto-retry validation, authentication, authorization, or deterministic conflicts.

## Developer experience
- Use discoverable TerraScale-specific names and immutable records. Validate options at construction with actionable errors.
- Fix first-party timeout, retry, concurrency, and buffer limits in code. Keep caller cancellation and security validation. Expose no tuning knobs.
- Provide XML docs for public APIs. Enable nullable reference types, analyzers, and warnings as errors.
- No console output or process-wide mutable state. Hidden background work is forbidden unless an explicitly owned reactive object starts and disposes it.

## Zero configuration (user decision 2026-10-09, do not revert)
- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles, or kill switches in code, config, or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; the node discovers all regions, infers its own from where it runs, and serves with no per-region enable step.
- OCI Vault creates, delivers, and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once there and picked up automatically. Nobody copies secrets by hand.
- One correct value per setting, in code. Expose no tuning knobs. Fakes, demo stores, and skip-auth exist only in test code and test binaries.
- CI rejects new switch-style configuration. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

Public regional endpoint discovery is automatic. Customer credentials authorize access; production service credentials arrive from OCI Vault. This contract is not evidence that every runtime flow has shipped; see [README.md](README.md) for SDK and qualification gaps. Existing production switches are inventoried for their owning implementation threads. Documentation changes must not invent APIs or bypass authorization, tenant scope, registered callbacks, payment consent, or safety guards.

## Model routing
- Short, bounded work (quick subagents, lookups, small edits): Claude Haiku 5.5 (`claude-haiku-5-5`). If unavailable, use OpenAI GPT-6.1-Sol via Codex (`gpt-6.1-sol`), after confirming the ID in the live model catalog.
- Never use Claude Haiku 4.5 (`claude-haiku-4-5`, any dated variant) or fall back silently to an older model. If neither model is available, stop and report it.
- Claude Opus 5.5 (`claude-opus-5-5`) advises on substantial work and runs any task expected to take over 10 minutes. The Haiku default never overrides this.
- T3 Code threads run in full-access mode, subject to tool safety constraints.
