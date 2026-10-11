# TerraScale Monorepo Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Monorepo

This repository (`TerraScale/terrascale`) holds four areas. The rules in this file apply to all of them. Read the area notes after this file.

| Area | Paths | What it is |
|---|---|---|
| libs | `src/TerraScale.Endpoints*`, `src/TerraScale.Logging`, `tests/TerraScale.Endpoints.Tests`, `tests/TerraScale.Logging.Tests`, `benchmark/dotnet/` | Shared .NET libraries: the endpoint framework and logging. Services consume them through ProjectReference. |
| auth | `src/TerraScale.Auth*`, `tests/TerraScale.Auth.{Fast,Slow,TestHarness}`, `docs/auth/`, `scripts/auth/` | ts-auth: the hosted identity provider. |
| terrabase | `src/TerraScale.TerraBase*`, `tests/TerraScale.TerraBase.{Fast,Slow,TestHarness}`, `docs/terrabase/`, `scripts/terrabase/` | TerraBase: the leaderless, multi-region document database. |
| benchmark | `benchmark/` (Go), `benchmark/dotnet/` (.NET library benchmarks) | All benchmark code, datasets, reports and results. |

Area notes: `src/TerraScale.TerraBase/AGENTS.md` (terrabase), `src/TerraScale.Auth/AGENTS.md` (auth).

### Where things go

- Projects: `src/<Project>/`. Test projects: `tests/<Project>/`. Scripts: `scripts/<area>/`. Area docs and their `README.md`: `docs/<area>/`. Benchmarks: `benchmark/` only.
- Shared files at the root: `terrascale.slnx`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig`, `BannedSymbols.txt`, `Makefile`, `.forgejo/workflows/`.
- Make: `make build`, `make test-fast`, `make test-slow` and `make check` are global. Per-area targets are `libs-*`, `auth-*`, `terrabase-*` and `benchmark-*`. Use the Makefile whenever an equivalent target exists.
- Auth and terrabase use the libs through ProjectReference, never NuGet packages.
- The ts-dotnet SDK stays a separate repository and is referenced from its sibling checkout (`TerraScaleRoot`, see `Directory.Build.props`).
- ts-dashboard and the SDKs (ts-go, ts-dotnet, ts-js, ts-rust, ts-cli) stay separate repositories.

## Rules for every area

### Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; it discovers every region, infers its own region from where it runs and starts serving without a per-region enable step.
- OCI Vault automatically creates, delivers and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically; nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

TerraBase discovers cluster membership and every serving region automatically; production authentication and persistence credentials arrive from OCI Vault.

- Shared libraries must preserve automatic regional discovery and OCI Vault secret delivery for consuming production services.
- Always-on capability does not bypass authentication, customer consent, repository permissions, credential revocation, validation, resource isolation, backups or reviewed recovery. Preserve branch/event identity, quality gates and failure/cleanup conditions in CI.

### Greenfield

- No shims, compatibility layers, or migration paths for old formats, unless a human asks.

### Public APIs and identifiers

- All public HTTP APIs are versioned under `/v1`. Breaking changes are explicit compatibility decisions, never incidental refactors.
- Every generated ID is a ULID.

### Data lives in TerraBase

- Every service keeps all its application data in TerraBase databases and collections: one collection per entity type, one document per record.
- No SQLite, local files, in-memory-only stores, Cloudflare KV/D1 or other databases for application data.
- The only exceptions are the products and the bootstrap layer named in the workspace rules: ts-status, ts-atlas and ts-cos, plus ts-orchestrator's NATS KV and OCI Object Storage.

### Service ownership

Each product owns one area. Never duplicate another product's data; call its API through the SDKs.

| Product | Owns |
|---|---|
| ts-auth | Users, organizations, projects, memberships, roles, invitations, SSO, OAuth/OIDC clients, signing keys, API keys (client credentials) |
| TerraBase | Databases, collections, documents, transactions, queries, backups, indexes, schema, usage metering |
| ts-dashboard | Platform UI and API, MCP server, plans, subscriptions, billing, spending caps |
| ts-orchestrator | Regions, hosts, releases, rollouts, service placement and scaling, edge |
| ts-atlas | Region discovery and the global `api.` / `auth.` proxies |
| SDKs (ts-go, ts-dotnet, ts-js, ts-cli) | Clients for every product API. Services talk to each other only through them |

- ts-auth never reads another product's storage. TerraBase authorizes from ts-auth tokens offline.

### General

- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Prefer simple, maintainable code and established libraries over custom solutions or unnecessary abstractions. Add an abstraction only when it expresses a real boundary or removes meaningful duplication.
- Follow repository conventions, keep changes focused, and avoid unrelated rewrites.
- Never invent APIs, package names, commands, SDK capabilities, or successful verification or test results.
- Preserve unrelated changes; never discard, overwrite, or commit another contributor's work.
- Do not claim a guarantee or property (durability, consistency, compatibility, security, feature completeness, protocol compliance, certification) without implementation and integration evidence that proves it.
- Never add production authentication bypasses, hardcoded credentials, or silently enabled demo behavior.

### Local CPU priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

### Dependencies

- Prefer mature, popular, actively maintained, and well-tested packages over custom implementations. Do not build functionality from scratch when a suitable established package exists, unless there is a clear technical reason.
- Use the latest stable, mutually compatible versions. Verify compatibility before upgrading.
- Builds MUST remain reproducible. See the .NET rules for lock files.

### Git

- Use `git` for repository operations and **`fj`** for Forgejo operations. Check CLI help when syntax is uncertain; do not invent commands.
- Prefer a dedicated Git worktree per task. Worktrees are the default, not mandatory: use the existing checkout when explicitly requested or when a worktree is impractical.
- Branch from the latest `origin/main`. Name branches `feat/`, `fix/`, `hotfix/` or `refactor/` plus a descriptive name.
- Keep each task's commits and PR isolated.
- Never implement task changes directly on or push directly to `main`.
- Remove a worktree only after confirming it has no uncommitted changes, no unpushed commits, and no files that must be kept.

### Pull Requests

- Target **`main`**.
- Include a change summary, validation performed, and the relevant compatibility, storage, migration or security implications.
- Never claim a check passed unless it actually ran successfully. Distinguish required checks from non-blocking Slow results.
- Required checks (formatting, build, analyzers, required security checks, OpenAPI validation where the area has it, and Fast) MUST pass before pushing. Do not wait for or require Slow.
- Update documentation and compatibility notes for API, storage or authentication behavior changes.
- Request reviews from **`mariogk`** and **`terrascale-bot`**, excluding the PR author.
- Enable auto-merge once required checks pass. Never bypass checks or required reviews.
- Before you finish, verify the target branch, title, reviewers, attachments and CI status. Send the PR URL and name any unresolved blockers or linked failure reports.

### Always use the Makefile

- Use the repository `Makefile` whenever an equivalent target exists.

## .NET rules (every .NET project: libs, auth, terrabase)

### Language and stack

- Target **.NET 11** (`net11.0`). The SDK is pinned in `global.json`.
- Use `System.Text.Json` with source-generated `JsonSerializerContext` metadata. Register every request and response type in the project's serializer context.
- **Newtonsoft.Json is banned.** Its APIs are listed in `BannedSymbols.txt`, enforced by the global `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`RS0030` as an error). `Directory.Build.targets` fails the build if any Newtonsoft package enters the graph, directly or transitively. Never remove these guards; choose a dependency that does not need Newtonsoft.
- Add newly banned APIs to `BannedSymbols.txt` with a reason that names the replacement.
- **Every** HTTP endpoint (client, administrative, health, examples) MUST use **`TerraScale.Endpoints`** (`src/TerraScale.Endpoints`, by ProjectReference). One endpoint class per file; compile-time registration through `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. Never use raw minimal-API `Map*` calls or controllers for endpoints. The only exceptions are framework-provided endpoints with no TerraScale.Endpoints equivalent: gRPC services (`MapGrpcService`), the Prometheus scrape, and the OpenAPI/Scalar documents.
- All public HTTP APIs MUST have **OpenAPI** documentation rendered through **Scalar**.

### Dependencies

- Versions are managed centrally in `Directory.Packages.props`. Never put a `Version` on a `PackageReference`.
- Prerelease packages only where .NET 11 requires them. Verify compatibility before upgrading.
- Commit every `packages.lock.json`. CI restores in locked mode.
- Package sources are fixed in `NuGet.config`. `TerraScale.*` packages restore only from the TerraScale Forgejo registry (`https://git.terrascale.tech/api/packages/TerraScale/nuget/index.json`); everything else restores only from nuget.org. Keep `packageSourceMapping` in sync when adding a source.
- Libraries that are not consumed as packages are project references.
- Shared MSBuild configuration lives in three root files. Keep project files minimal.
  - `Directory.Build.props`: common properties for every project (framework, language, analyzers, lock files, `TerraScaleRoot`).
  - `Directory.Build.targets`: rules that depend on project properties (the endpoints wiring and the test-project wiring).
  - `Directory.Packages.props`: every package version.

### Build

- Analyzers and code style run in the build with warnings as errors (`Directory.Build.props`, `.editorconfig`). `TreatWarningsAsErrors` MUST always be `true` for every project and configuration. Never disable it, override it per project, or use `WarningsNotAsErrors`.
- `NoWarn` is closed. The only entries are the ones in `Directory.Build.props` and `Directory.Build.targets`, each with its reason in a comment. Never add another `NoWarn` entry without asking a human first.
- Never suppress warnings globally by other means: no `GlobalSuppressions.cs` or assembly-level `[SuppressMessage]`, no `.editorconfig` severity lowered to `none`/`silent`/`suggestion` for an enforced rule, no `<WarningLevel>` reduction, and no `#pragma warning disable` outside generated code. When a single local suppression is truly required, ask a human first and document the justification next to it.
- `make check` (formatting, build, fast suite) MUST pass before pushing.

## Testing (every area)

> [!IMPORTANT]
> **Services (auth, terrabase) MUST NEVER have unit tests. Only INTEGRATION tests are allowed, and every test in every area MUST run in parallel.**

- Use **TUnit** for tests and **Bogus** for synthetic test data.
- Run suites with `make test-fast` / `make test-slow`. They use `dotnet run --project`, which is faster than `dotnet test` for TUnit.

### Parallel execution

- Every test MUST be safe to run in parallel with every other test, in both suites. TUnit runs tests in parallel by default; keep it that way.
- NEVER use `[NotInParallel]`, `[ParallelLimiter]`, sequential execution, or lower the parallelism to make a test pass. Fix the isolation problem instead.
- Each test owns its resources: its own server instance (`TerraBaseTestServer.StartAsync()`), ephemeral ports (`0`), temporary directories, buckets/prefixes, databases and synthetic data. Clean them up when the test ends.
- Never depend on test ordering, shared mutable static state, fixed ports, wall-clock sleeps, or another test's data.

### Services: two suites (auth, terrabase)

Each service maintains exactly two integration suites:

- **Fast** (`tests/TerraScale.Auth.Fast`, `tests/TerraScale.TerraBase.Fast`)
  - Covers critical paths and common user workflows, and stays fast enough to run routinely.
  - MUST run before every merge, deployment, release, and relevant CI action. Results MUST cover the current changes or the exact commit being advanced.
  - Any failure MUST block the operation.
- **Slow** (`tests/TerraScale.Auth.Slow`, `tests/TerraScale.TerraBase.Slow`)
  - Provides comprehensive integration and end-to-end coverage.
  - MUST run independently from the release-critical pipeline and MUST NOT block merges, deployments, or releases.
  - Preserve visible failures: non-blocking does not mean passing.
  - Any failure MUST automatically create a Forgejo issue with `fj`, or update the existing open report for the same failure. The issue contains the failed tests, commit SHA, environment, reproduction steps, expected and actual behavior, redacted logs and the CI run URL. The reporting step runs even when the test step fails.
  - Keep sensitive vulnerability details in the approved private reporting channel, never in public issues.
- Shared test utilities live in each service's TestHarness (`tests/TerraScale.Auth.TestHarness`, `tests/TerraScale.TerraBase.TestHarness`) and are linked as source. They MUST NOT become a third suite.
- New functionality MUST be covered by the appropriate suite. Critical or commonly used functionality SHOULD be in Fast. Broader, expensive, edge-case, stress, or exhaustive scenarios SHOULD be in Slow.
- Never skip, disable, or weaken tests to bypass a failure.

### Libraries

- The libs keep their own test projects (`tests/TerraScale.Endpoints.Tests`, `tests/TerraScale.Logging.Tests`). Their tests follow the parallel-execution rules above.

## Benchmarks

- ALL benchmark code, datasets, reports, and results live in `benchmark/`: Go code at the top level, .NET library benchmarks in `benchmark/dotnet/`. Do not add them anywhere else in this repository.
- Save every benchmark result as a `.md` table in `benchmark/bench/`, so differences and improvements can be compared from the very beginning.
- Performance comparisons and historical results remain in `benchmark/`.
- The operating contract requires automatic serving-region discovery. OCI Vault delivers production benchmark credentials. Fakes and synthetic modes belong only in test binaries.
