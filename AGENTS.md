# TerraScale monorepo

Shipping: follow the workspace [Shipping code section](/home/mariogk/Projects/TerraScale/AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check).

## Layout and commands

- Libraries: `src/TerraScale.Endpoints*`, `src/TerraScale.Logging` and their tests. Read `src/TerraBase/AGENTS.md` and `src/TerraScale.Authentication/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, tests in `tests/<Project>/`, scripts in `scripts/<area>/`, and docs in `docs/<area>/`. All benchmark code, datasets and results belong in `benchmark/`; .NET benchmarks use `benchmark/dotnet/`. Save comparable Markdown result tables in `benchmark/bench/`.
- Use the root Makefile when a target exists. Build: `make build`; Fast: `make test-fast`; Slow: `make test-slow`; full checks: `make check`. Area targets: `libs-build`, `libs-test`, `libs-check`, `auth-build`, `auth-test-fast`, `auth-test-slow`, `auth-check`, `terrabase-build`, `terrabase-test-fast`, `terrabase-test-slow`, `terrabase-check`, `benchmark-build`, `benchmark-check`.
- Fast pre-push command: `make test-fast`, or the changed area's `make libs-test`, `make auth-test-fast` or `make terrabase-test-fast`.
- Auth and TerraBase consume the libraries through ProjectReference. ts-dotnet remains a sibling repo, consumed as source via `TsDotnetRoot` in `Directory.Build.props`. Keep SDKs and dashboard separate.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- Every feature is always on. No feature flags, opt-in settings, enabled booleans, deploy toggles or kill switches. Delete their guarded paths. Values have one correct setting in code; expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test binaries.
- Derive region, node and peers from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE` (one `<node> <region> <netbird-ipv4>` line per peer). Every region serves automatically. Libraries must preserve automatic discovery for services and benchmarks.
- CI rejects switch-style configuration; unavoidable third-party names need a narrow allowance and one-line reason. Always-on behavior preserves authentication, consent, permissions, revocation, validation, isolation, backups and recovery safeguards. Preserve CI branch/event identity, quality gates and cleanup conditions.
- Greenfield: no old-format shims, compatibility layers or migrations unless requested. Public HTTP APIs use `/v1`; breaking API changes are explicit compatibility decisions. Generated IDs are ULIDs.
- Application data uses TerraBase databases and collections: one collection per entity type and one document per record. No alternate database, local-file or memory-only application store. TerraBase external persistence uses Tigris. The separate ts-status service retains its D1 exception.
- Each product owns its data. ts-auth owns identity/access, organizations, projects and API credentials. TerraBase owns databases, documents, queries, transactions, backups, indexes, schema and usage metering. Dashboard owns platform UI/API/MCP, plans, subscriptions, billing and spending caps. ts-orch owns placement, scaling and rollout. Services call other products through SDKs; never read their storage. TerraBase validates ts-auth tokens offline.
- Prefer mature maintained packages and simple code. Add abstractions only for real boundaries or meaningful duplication. Verify APIs, dependencies and claimed guarantees against implementation and integration evidence. Use mutually compatible current versions and reproducible builds.
- Update API, storage and authentication docs with behavior changes. Shared docs get new files listed by their index instead of conflicting in-place edits. Workflow changes go through the CI owner.

## .NET

- Target `net11.0` using the exact SDK in `global.json`. Use source-generated `System.Text.Json` metadata and register every request/response type in its serializer context.
- Newtonsoft is banned. Preserve `BannedSymbols.txt`, RS0030 enforcement and the package-graph guard in `Directory.Build.targets`. New banned APIs need a reason and replacement.
- Every HTTP endpoint, including health, admin and examples, uses `TerraScale.Endpoints` via ProjectReference: one endpoint class per file; register with `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. No raw minimal API handlers or controllers. Exceptions: gRPC, Prometheus scraping and OpenAPI/Scalar documents.
- Public HTTP APIs need OpenAPI rendered by Scalar.
- Manage versions only in `Directory.Packages.props`; no Version on PackageReference. Prerelease packages only where .NET 11 requires them. Commit lock files; restore locked. `NuGet.config` uses nuget.org; keep source mappings correct if a source is added.
- Keep project files minimal. Use ProjectReference for libraries not consumed as packages.
- Warnings are errors in every configuration. Never lower warning levels or weaken analyzer/style enforcement. `NoWarn` is limited to existing justified entries in shared build files. No global suppressions, assembly suppressions, enforced-rule severity reductions or warning pragmas outside generated code. Any necessary local suppression needs a documented justification and human approval.

## Tests

- Use TUnit and Bogus. Service tests are integration/end-to-end only. Run TUnit through `dotnet run --project` (the Makefile does this).
- Tests must be safe alongside every other test. Keep default parallelism; no `NotInParallel`, `ParallelLimiter`, sequential execution or lower parallelism to hide isolation bugs. Each test owns its servers, ephemeral ports, directories, buckets/prefixes, databases and synthetic data, then cleans up. No ordering dependencies, mutable static state, fixed ports or wall-clock sleeps.
- Auth and TerraBase each have Fast and Slow integration suites plus source-linked TestHarness utilities; the harness is not another suite. Library tests stay in `tests/TerraScale.Endpoints.Tests` and `tests/TerraScale.Logging.Tests`.
- Put critical/common behavior in Fast, expensive/exhaustive scenarios in Slow. Cover new functionality. Never skip, disable or weaken tests to bypass failures.
- Slow runs independently and is non-blocking, with visible failures. A failure must create or update a matching GitHub issue with tests, SHA, environment, reproduction, expected/actual behavior, redacted logs and run URL, even when tests fail. Sensitive vulnerability details use the approved private channel.
