<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Ship with `ts-ship` (fetch, rebase, affected Fast suites on a dev host, push `main`, retry on races). Docs-only, `.cos`-only and `AGENTS.md`-only changes skip tests. Never force-push or delete `main`.
- CI runs on every main push; deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Heavy builds, tests, benchmarks and stress repeats use `ts-heavy-any`, never GitHub Actions. Use mariogk-dev or beta; never run anything on or SSH to mariogk-desktop.
- Fast tests run fully parallel, finish in under 5 minutes and cover only the main features. Everything else goes to Slow; a Fast suite over 5 minutes is a bug.
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`. For page changes, report before/after screenshots.
- StorageFormat.cs bumps and production resets belong to the lane in `.cos/lanes.md`; other lanes put `Needs format bump` in the commit message.

### Agents and decisions

- Use gpt-6.1-sol (Codex, high effort; low for work under 10 minutes) for most work. Use claude-haiku-5-5 for bounded work of at most 20 minutes; never Haiku 4.5 or the `haiku` alias. Reserve claude-opus-5-5 for engine/replication, auth/security design, billing correctness, production infrastructure or work Sol already failed.
- Decide and continue. Record `DECISION: <choice>. Reason: <why>` in commits.
- Workers never ask the user. Send the parent `NEED Q-<ID>: <question> | options: 1) <recommended> 2) ... | DECIDED: 1, continuing`. Only the COS asks the user.
- Block only for a user-only secret/login, new paid capacity, or deleting serving infrastructure or unrecoverable data. Start login device flows yourself, show URL/code, then finish after the user enters them.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Dev secrets: `ts-dev-secret get <name> | <consumer>` or `ts-dev-secret env <name> -- <cmd>`.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops. Never revive ts-reviewer or ts-cos.

<!-- shared-rules:end -->

# TerraScale monorepo


## Layout and commands

- Libraries: `src/TerraScale.Endpoints*`, `src/TerraScale.Logging` and their tests. Read `src/TerraBase/AGENTS.md` and `src/TerraScale.Authentication/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, tests in `tests/<Project>/`, scripts in `scripts/<area>/`, and docs in `docs/<area>/`. All benchmark code, datasets and results belong in `benchmark/`; .NET benchmarks use `benchmark/dotnet/`. Save comparable Markdown result tables in `benchmark/bench/`.
- Use the root Makefile when a target exists. Build: `make build`; Fast: `make test-fast`; Slow: `make test-slow`; full checks: `make check`. Area targets: `libs-build`, `libs-test`, `libs-check`, `auth-build`, `auth-test-fast`, `auth-test-slow`, `auth-check`, `terrabase-build`, `terrabase-test-fast`, `terrabase-test-slow`, `terrabase-check`, `benchmark-build`, `benchmark-check`.
- Fast pre-push command: `make test-fast`, or the changed area's `make libs-test`, `make auth-test-fast` or `make terrabase-test-fast`.
- SDKs live in `sdk/dotnet`, `sdk/js`, `sdk/go`, `sdk/python` and `sdk/rust`; read each area's `AGENTS.md`. Fast: `make sdk-dotnet-test-fast`, `make sdk-js-test-fast`, `make sdk-go-test-fast`, `make sdk-python-test-fast`, `make sdk-rust-test-fast`; each finishes within five minutes. `make sdk-test-fast` runs all five.
- Auth and TerraBase consume the libraries through ProjectReference. Services consume sdk/dotnet from the same monorepo revision via `TsDotnetRoot` in `Directory.Build.props`.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- Never fork the Turso database: use upstream tursodatabase/turso at a pinned commit, tarball verified by SHA-256 at build time (benchmark/turso-sync-host/turso-pin.env). Only the Turso client may be forked (today: the vendored driver in src/vendor/Turso.Data.Sqlite), and only when upstream can't do what we need.
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

- Libs Fast must finish in at most 5 minutes; keep it there.
- Use TUnit and Bogus. Service tests are integration/end-to-end only. Run TUnit through `dotnet run --project` (the Makefile does this).
- Tests must be safe alongside every other test. Keep default parallelism; no `NotInParallel`, `ParallelLimiter`, sequential execution or lower parallelism to hide isolation bugs. Servers may be shared per assembly or in a small pool; each test owns its database, tenant or prefix and synthetic data, then cleans up. Server fixtures own their ephemeral ports, directories and lifecycle. No ordering dependencies, mutable static state outside fixture-owned resources, fixed ports or wall-clock sleeps.
- Auth and TerraBase each have Fast and Slow integration suites plus source-linked TestHarness utilities; the harness is not another suite. Library tests stay in `tests/TerraScale.Endpoints.Tests` and `tests/TerraScale.Logging.Tests`.
- Put critical/common behavior in Fast, expensive/exhaustive scenarios in Slow. Cover new functionality. Never skip, disable or weaken tests to bypass failures.
- Slow runs independently and is non-blocking, with visible failures. A failure must create or update a matching GitHub issue with tests, SHA, environment, reproduction, expected/actual behavior, redacted logs and run URL, even when tests fail. Sensitive vulnerability details use the approved private channel.
