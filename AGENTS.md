# TerraScale Monorepo Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Monorepo

This repository (`TerraScale/terrascale`) holds four areas; these rules apply to all of them. Area notes, read next: `src/TerraBase/AGENTS.md` (terrabase) and `src/TerraScale.Authentication/AGENTS.md` (auth).

| Area | Paths | What it is |
|---|---|---|
| libs | `src/TerraScale.Endpoints*`, `src/TerraScale.Logging`, `tests/TerraScale.Endpoints.Tests`, `tests/TerraScale.Logging.Tests`, `benchmark/dotnet/` | Shared .NET endpoint framework and logging, consumed through ProjectReference. |
| auth | `src/TerraScale.Authentication*`, `tests/TerraScale.Authentication.{Fast,Slow,TestHarness}`, `docs/auth/`, `scripts/auth/` | ts-auth: the hosted identity provider. |
| terrabase | `src/TerraBase*`, `tests/TerraBase.{Fast,Slow,TestHarness}`, `docs/terrabase/`, `scripts/terrabase/` | TerraBase: the leaderless, multi-region document database. |
| benchmark | `benchmark/` (Go), `benchmark/dotnet/` (.NET) | All benchmark code, datasets, reports and results. |

### Where things go

- Projects in `src/<Project>/`, tests in `tests/<Project>/`, scripts in `scripts/<area>/`, area docs and `README.md` in `docs/<area>/`, benchmarks in `benchmark/` only.
- Shared root files: `TerraScale.slnx`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig`, `BannedSymbols.txt`, `Makefile`, `.github/workflows/`.
- Make: `make build`, `make test-fast`, `make test-slow` and `make check` are global; per-area targets are `libs-*`, `auth-*`, `terrabase-*` and `benchmark-*`. Use the Makefile whenever an equivalent target exists.
- Auth and terrabase use the libs through ProjectReference, never NuGet packages. The ts-dotnet SDK stays a separate repository, referenced from its sibling checkout (`TerraScaleRoot`, see `Directory.Build.props`). ts-dashboard and the SDKs (ts-go, ts-dotnet, ts-js, ts-rust, ts-cli) also stay separate.

## Agent model routing

- Short, bounded work (quick subagents, lookups, small edits): Claude Haiku 5.5, `claude-haiku-5-5`.
- If `claude-haiku-5-5` is unavailable: OpenAI GPT-6.1-Sol through Codex, `gpt-6.1-sol`, after confirming the ID in the live model catalog.
- Never use Claude Haiku 4.5 under any ID (`claude-haiku-4-5`, `claude-haiku-4-5-20251001`, or any other dated variant), and never fall back silently to an older model. If neither model is available, stop and report it.
- Claude Opus 5.5 (`claude-opus-5-5`) is the advisor and runs every task expected to take more than 10 minutes. The Haiku default for short work never overrides this.
- Every T3 Code thread runs in full-access mode, subject to tool safety constraints.

## Platform

- Everything runs on OCI under ts-orch ([TerraScale/ts-orchestrator](https://github.com/TerraScale/ts-orchestrator)). Regions: `gru` (`sa-saopaulo-1`), `sjc` (`us-sanjose-1`) and `fra` (`eu-frankfurt-1`).
- Services: `ts-terrabase` and `ts-auth`, one node per region on the ts-orch hosts, joined by a private NetBird mesh.
- CI is GitHub Actions on GitHub-hosted runners: it builds and tests, then publishes images to GHCR. ts-orch admits the image and rolls it out to `gru`, `sjc` and `fra` automatically. A failed readiness check stops the rollout; fix forward.
- Tigris is the only Fly.io dependency: Tigris object storage keeps its Fly organization connection (`flyctl storage`, `fly.storage.tigris.dev`).

## Rules for every area

### Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. ts-orch sets `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE`; nodes infer their region and node name from them, find every peer region in the peers file (one `<node> <region> <netbird-ipv4>` line each) and serve without a per-region enable step. Current regions: `gru`, `sjc` and `fra`.
- OCI Vault is the only secret store. ts-orch delivers secrets to each service's environment through instance principals; nobody copies them into code or config. Store vendor-issued keys (such as Stripe and Cloudflare) once in OCI Vault. Admin access to a running host is NetBird SSH (`ts-orch netbird ssh`) only.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration. Only unavoidable third-party names may have a narrow allowance with a one-line reason.
- TerraBase discovers cluster membership and every serving region automatically; production credentials arrive from OCI Vault through ts-orch. Shared libraries must preserve this for consuming production services.
- Always-on capability does not bypass authentication, customer consent, repository permissions, credential revocation, validation, resource isolation, backups or reviewed recovery. Preserve branch/event identity, quality gates and failure/cleanup conditions in CI.

### Identifiers, APIs and data

- Greenfield: no shims, compatibility layers, or migration paths for old formats, unless a human asks.
- All public HTTP APIs are versioned under `/v1`. Breaking changes are explicit compatibility decisions, never incidental refactors. Every generated ID is a ULID.
- Every service keeps all application data in TerraBase databases and collections: one collection per entity type, one document per record. No SQLite, local files, in-memory-only stores, Cloudflare KV/D1 or other databases for application data.
- Exception: ts-status (Cloudflare Worker + D1). TerraBase's own external persistence (snapshots, logs, backups) goes to Tigris, the only object store.

### Service ownership

Each product owns one area. Never duplicate another product's data; call its API through the SDKs. ts-auth never reads another product's storage, and TerraBase authorizes from ts-auth tokens offline.

| Product | Owns |
|---|---|
| ts-auth | Users, organizations, projects, memberships, roles, invitations, SSO, OAuth/OIDC clients, signing keys, API keys (client credentials) |
| TerraBase | Databases, collections, documents, transactions, queries, backups, indexes, schema, usage metering |
| ts-dashboard | Platform UI and API, MCP server, plans, subscriptions, billing, spending caps |
| ts-orch (OCI) | Regions, placement, scaling, rollout, edge, TLS, secrets (OCI Vault) |
| SDKs (ts-go, ts-dotnet, ts-js, ts-cli) | Clients for every product API. Services talk to each other only through them |

### General

- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Keep changes focused and follow repository conventions; avoid unrelated rewrites. Preserve unrelated changes; never discard, overwrite, or commit another contributor's work.
- Prefer simple, maintainable code. Use mature, popular, actively maintained, well-tested packages rather than building from scratch, unless there is a clear technical reason. Add an abstraction only for a real boundary or meaningful duplication removal.
- Never invent APIs, package names, commands, SDK capabilities, or successful verification or test results. Do not claim a guarantee or property (durability, consistency, compatibility, security, feature completeness, protocol compliance, certification) without implementation and integration evidence.
- Never add production authentication bypasses, hardcoded credentials, or silently enabled demo behavior.
- Use the latest stable, mutually compatible versions and verify compatibility before upgrading. Builds MUST remain reproducible.

### Local CPU priority

- Prefix every local build, test, lint, benchmark and dev server with `nice -n 19` (for example `nice -n 19 make test-fast`).
- In CI (`CI=true`), do nothing. Never add `nice` or `renice` to CI workflows, Makefiles, scripts or Containerfiles.

### Git

- Use `git` for repository operations and **`gh`** for GitHub operations. Check CLI help when syntax is uncertain; do not invent commands.
- Prefer a dedicated worktree per task; use the existing checkout only when explicitly requested or when a worktree is impractical. Remove a worktree only after confirming no uncommitted changes, no unpushed commits and no files that must be kept.
- Branch from the latest `origin/main`, named `feat/`, `fix/`, `hotfix/` or `refactor/` plus a descriptive name. Keep each task's commits and PR isolated. Never implement changes directly on, or push directly to, `main`.

### Pull requests

- Target **`main`**. Include a change summary, validation performed, and the relevant compatibility, storage, migration or security implications.
- **Start from latest main.** Base every change on the most recent `main`: before starting, `git fetch origin` and branch from `origin/main`, never from a stale local `main`, another feature branch or an old worktree. Before every push, `git fetch origin && git rebase origin/main` again; right before merging, rebase once more and merge only when CI is green on that exact head.
- Never claim a check passed unless it actually ran successfully. Distinguish required checks from non-blocking Slow results.
- CI is the gate: no local test/check run is required before pushing or opening a PR. Required checks (formatting, build, analyzers, required security checks, OpenAPI validation where the area has it, and Fast) must pass in CI before merge. Do not wait for or require Slow.
- Update documentation and compatibility notes for API, storage or authentication behavior changes.
- Enable auto-merge once required checks pass. Never bypass checks.
- Before finishing, verify the target branch, title, attachments and CI status. Send the PR URL and name any unresolved blockers or linked failure reports.

### PR previews (required for every visible change)

- Every PR that changes anything a user sees (pages, components, styles, layout, copy, emails, generated pages, maps, charts) MUST embed previews in the PR body. A visible change without previews is not ready to merge.
- Before and after screenshots of each changed page and state, desktop (1440 px wide) and mobile (390 px wide); light and dark too where the app has both. Add a short video or GIF for interactions (toggles, menus, flows, animations).
- Capture from the real running build of the exact PR head (dev server, preview deploy or live preview), never mockups. Use synthetic or demo accounts; no secrets, tokens or real customer data on screen.
- Upload: commit the images to this repo's orphan branch `pr-assets` under `<pr-number>/` (first time: `git switch --orphan pr-assets`); never put them on the PR branch or `main`, never merge `pr-assets`. Embed each as `![<page> <state> after, desktop](https://raw.githubusercontent.com/TerraScale/terrascale/pr-assets/<pr-number>/<file>.png)` and check that every URL returns 200 before posting.
- When a later push changes the visuals, replace the previews. PRs with no visible change say "No visual change" in the body.

### Conflict rules

- Keep each PR under about 400 changed lines. Stack bigger work as a series of PRs.
- Feature PRs never bump `src/TerraBase/Engine/Sqlite/StorageFormat.cs`. Write `Needs format bump` in the PR body; the deploy lane batches bumps, one production reset per batch.
- Shared docs get a new file, not in-place edits (for example `docs/terrabase/replication/`, listed by its index).
- Changes to `.github/workflows/ci.yml` go through the CI owner.

## .NET rules (libs, auth, terrabase)

### Language and stack

- Target **.NET 11** (`net11.0`), SDK pinned in `global.json`. Use `System.Text.Json` with source-generated `JsonSerializerContext` metadata; register every request and response type in the project's serializer context.
- **Newtonsoft.Json is banned.** Its APIs are listed in `BannedSymbols.txt` and enforced by `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`RS0030` as an error). `Directory.Build.targets` fails the build if any Newtonsoft package enters the graph. Never remove these guards. Add newly banned APIs to `BannedSymbols.txt` with a reason naming the replacement.
- **Every** HTTP endpoint (client, administrative, health, examples) MUST use **`TerraScale.Endpoints`** (`src/TerraScale.Endpoints`, by ProjectReference): one endpoint class per file, registered through `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. Never use raw minimal-API `Map*` calls or controllers. Exceptions with no equivalent: gRPC (`MapGrpcService`), the Prometheus scrape, and the OpenAPI/Scalar documents.
- All public HTTP APIs MUST have **OpenAPI** documentation rendered through **Scalar**.

### Dependencies

- Versions are managed in `Directory.Packages.props`. Never put a `Version` on a `PackageReference`.
- Prerelease packages only where .NET 11 requires them, verified before upgrading.
- Commit every `packages.lock.json`; CI restores in locked mode.
- `NuGet.config` lists nuget.org only. Keep `packageSourceMapping` in sync when adding a source.
- Libraries not consumed as packages are project references. Keep project files minimal; shared MSBuild configuration lives in `Directory.Build.props` (common properties, `TerraScaleRoot`), `Directory.Build.targets` (property-dependent endpoints and test-project wiring) and `Directory.Packages.props` (every version).

### Build

- Analyzers and style run in the build with warnings as errors (`Directory.Build.props`, `.editorconfig`). `TreatWarningsAsErrors` MUST always be `true` for every project and configuration. Never disable it, override it per project, or use `WarningsNotAsErrors`.
- `NoWarn` is closed: only the entries in `Directory.Build.props` and `Directory.Build.targets`, each with its reason in a comment. Never add another without asking a human first.
- Never suppress warnings globally or otherwise: no `GlobalSuppressions.cs`, assembly-level `[SuppressMessage]`, `.editorconfig` severity lowered to `none`/`silent`/`suggestion` for an enforced rule, `<WarningLevel>` reduction, or `#pragma warning disable` outside generated code. If one local suppression is truly required, ask a human first and document the justification beside it.

## Testing (every area)

> [!IMPORTANT]
> **Services (auth, terrabase) MUST NEVER have unit tests. Only INTEGRATION tests are allowed, and every test in every area MUST run in parallel.**

- Use **TUnit** for tests and **Bogus** for synthetic data. Run suites with `make test-fast` / `make test-slow`, which use `dotnet run --project` (faster than `dotnet test` for TUnit).

### Parallel execution

- Every test MUST be safe to run in parallel with every other test, in both suites. Keep TUnit's default parallelism.
- NEVER use `[NotInParallel]`, `[ParallelLimiter]`, sequential execution, or lower parallelism to make a test pass. Fix the isolation problem instead.
- Each test owns its resources (its own server via `TerraBaseTestServer.StartAsync()`, ephemeral ports `0`, temp directories, buckets/prefixes, databases, synthetic data) and cleans them up.
- Never depend on test ordering, shared mutable static state, fixed ports, wall-clock sleeps or another test's data.

### Services: two suites (auth, terrabase)

Each service maintains exactly two integration suites:

- **Fast** (`tests/TerraScale.Authentication.Fast`, `tests/TerraBase.Fast`): critical paths and common workflows, fast enough to run routinely. MUST run before every merge, deployment, release and relevant CI action, covering the current changes or the exact commit being advanced. Any failure MUST block the operation.
- **Slow** (`tests/TerraScale.Authentication.Slow`, `tests/TerraBase.Slow`): comprehensive integration and end-to-end coverage. MUST run independently of the release-critical pipeline and MUST NOT block merges, deployments or releases. Non-blocking does not mean passing: preserve visible failures.
  - Any failure MUST automatically create a GitHub issue with `gh`, or update the existing open report for the same failure. The issue holds failed tests, commit SHA, environment, reproduction steps, expected and actual behavior, redacted logs and the CI run URL. The reporting step runs even when the test step fails.
  - Keep sensitive vulnerability details in the approved private reporting channel, never in public issues.
- Shared utilities live in each service's TestHarness (`tests/TerraScale.Authentication.TestHarness`, `tests/TerraBase.TestHarness`), linked as source. They MUST NOT become a third suite.
- New functionality MUST be covered by the appropriate suite. Critical or common behavior SHOULD be in Fast; broader, expensive, edge-case, stress or exhaustive scenarios SHOULD be in Slow.
- Never skip, disable, or weaken tests to bypass a failure.

### Libraries

- The libs keep their own test projects (`tests/TerraScale.Endpoints.Tests`, `tests/TerraScale.Logging.Tests`) under the parallel-execution rules above.

## Benchmarks

- ALL benchmark code, datasets, reports and results live in `benchmark/`: Go at the top level, .NET library benchmarks in `benchmark/dotnet/`. Do not add them anywhere else in this repository.
- Save every result as a `.md` table in `benchmark/bench/` so differences stay comparable from the start. Comparisons and history remain in `benchmark/`.
- Benchmarks require automatic serving-region discovery from the ts-orch peers file. OCI Vault delivers production benchmark credentials. Fakes and synthetic modes belong only in test binaries.
