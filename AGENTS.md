<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- Ship every change as a pull request to `main`. Fetch `origin` first and branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution. Ignore outside contributors' PRs unless asked.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Before pushing, build or type-check only the project you changed. Don't run test suites locally: GitHub CI runs them on the PR. A PR squash-merges as soon as its checks are green and its review passed; rebase it when it conflicts. Never push directly to, force-push or delete `main`.
- Deploy only green main. If your merge makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to mariogk-desktop.
- StorageFormat.cs bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Code

- No comments: no `//`, `#`, `/* */`, `///` doc comments or commented-out code in code, scripts or build files, including code you move or rewrite. Names and types carry the meaning; non-obvious choices go in the commit message as `DECISION:`.
- Fix the root cause. No workarounds: no retries, sleeps, longer timeouts, swallowed errors, special cases, flags or skipped or weakened tests that hide a symptom. If the cause is outside your task or outside our code, report it instead of patching around it.
- No legacy: no deprecated paths, compatibility shims, fallbacks, old-format readers, dead code or copies kept "for now". Replace the old path and delete it in the same change.
- No unit tests. Tests drive the real service through its public interface (integration or end-to-end); never test a single class or function in isolation or mock our own code. Delete unit tests you come across; cover a critical path they guarded with an integration test.

### Tests

- Keep tests few. Fast covers only the happy path and critical paths. Slow keeps only heavy critical tests; delete everything else.
- A new feature adds at most one happy-path and one critical-failure test. Refactors, renames, docs and config changes add none. Deleting low-value tests is welcome.
- Fast tests run fully parallel. Each single test finishes in under 10 s, each area's Fast suite in under 30 s, and every CI test job in under 5 minutes. Anything slower is a bug.
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`.
- UI changes: screenshot every changed page before (`origin/main`) and after (your branch), same viewport, data and theme, in every supported theme. Save the PNGs outside the repo as `~/.local/state/shots/<branch>/<page>-before.png` and `-after.png`; never commit screenshots. Show them in your T3 thread: if you have T3 tools, `html_render` a before/after grid (absolute local image paths are inlined); otherwise embed each as a Markdown image with its absolute path, e.g. `![after: /settings](/home/<user>/.local/state/shots/<branch>/settings-after.png)`, in your final reply. List the pages in the PR body.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

<!-- shared-rules:end -->

# TerraScale monorepo

## Development skills

Step-by-step guides live in `.agents/skills/` (also linked from `.claude/skills/`):

- `add-feature`: build a feature end to end (owner, contract, storage, SDKs, UI, docs, tests).
- `add-endpoint`: add an HTTP endpoint with TerraScale.Endpoints.
- `sdk-parity`: add or change an operation across the five SDKs.
- `simple-ui`: design and build dashboard, auth and site screens.

## Layout and commands

- Libraries: `src/TerraScale.Endpoints*`, `src/TerraScale.Logging` and their tests. Read `src/TerraBase/AGENTS.md` and `src/TerraScale.Authentication/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, tests in `tests/<Project>/`, scripts in `scripts/<area>/`, and docs in `docs/<area>/`. All benchmark code, datasets and results belong in `benchmark/`; .NET benchmarks use `benchmark/dotnet/`. Save comparable Markdown result tables in `benchmark/bench/`.
- Use the root Makefile when a target exists. Build: `make build`; Fast: `make test-fast`; Slow: `make test-slow`; full checks: `make check`. Area targets: `libs-build`, `libs-test`, `libs-check`, `auth-build`, `auth-test-fast`, `auth-test-slow`, `auth-check`, `terrabase-build`, `terrabase-test-fast`, `terrabase-test-slow`, `terrabase-check`, `benchmark-build`, `benchmark-check`.
- Fast: `make test-fast`, or the changed area's `make libs-test`, `make auth-test-fast` or `make terrabase-test-fast`.
- SDKs live in `sdk/dotnet`, `sdk/js`, `sdk/go`, `sdk/python` and `sdk/rust`; read each area's `AGENTS.md`. Fast: `make sdk-dotnet-test-fast`, `make sdk-js-test-fast`, `make sdk-go-test-fast`, `make sdk-python-test-fast`, `make sdk-rust-test-fast`; each finishes in under 30 s wall time, with each single test in under 10 s. `make sdk-test-fast` runs all five.
- Auth and TerraBase consume the libraries through ProjectReference. ts-auth consumes sdk/dotnet from the same monorepo revision via `TsDotnetRoot` in `Directory.Build.props`.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- Never fork the Turso database: use upstream tursodatabase/turso at a pinned commit, tarball verified by SHA-256 at build time (benchmark/turso-sync-host/turso-pin.env). Only the Turso client may be forked (today: the vendored driver in src/vendor/Turso.Data.Sqlite), and only when upstream can't do what we need.
- Every feature is always on. No feature flags, opt-in settings, enabled booleans, deploy toggles or kill switches. Delete their guarded paths. Values have one correct setting in code; expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test binaries.
- Derive region, node and peers from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE` (one `<node> <region> <netbird-ipv4>` line per peer). Every region serves automatically. Libraries must preserve automatic discovery for services and benchmarks.
- CI rejects switch-style configuration; unavoidable third-party names need a narrow allowance and one-line reason. Always-on behavior preserves authentication, consent, permissions, revocation, validation, isolation, backups and recovery safeguards. Preserve CI branch/event identity, quality gates and cleanup conditions.
- Greenfield: no old-format shims, compatibility layers or migrations unless requested. Public HTTP APIs use `/v1`; breaking API changes are explicit compatibility decisions. Generated IDs are ULIDs.
- Application data lives in TerraBase databases. Platform services store it in relational tables through the TerraBase EF Core provider (`sdk/dotnet/src/TerraScale.EntityFrameworkCore`): one `DbContext` per service, typed columns, keys, foreign keys and constraints for the invariants, and committed migrations applied at startup. Never edit, rename or delete a merged migration; add a new one (`scripts/ci/migrations-frozen.py` fails the PR). No alternate database, local-file or memory-only application store. TerraBase external persistence uses Tigris. The separate ts-status service retains its D1 exception.
- Each product owns its data. ts-auth owns identity/access, organizations, projects and API credentials. TerraBase owns databases, documents, queries, transactions, backups, indexes, schema and usage metering. Dashboard owns platform UI/API/MCP, plans, subscriptions, billing and spending caps. ts-orch owns placement, scaling and rollout. Services call other products through their APIs (the dashboard calls TerraBase over MagicOnion through the shared contracts library); never read their storage. TerraBase validates ts-auth tokens offline.
- Prefer mature maintained packages and simple code. Add abstractions only for real boundaries or meaningful duplication. Verify APIs, dependencies and claimed guarantees against implementation and integration evidence. Use mutually compatible current versions and reproducible builds.
- UI work follows `.agents/skills/simple-ui/SKILL.md`: simple purpose, architecture, design and implementation; no taglines, helper text or secondary labels. If a screen needs them, fix the layout.
- Update API, storage and authentication docs with behavior changes. Shared docs get new files listed by their index instead of conflicting in-place edits. Workflow changes go through the CI owner.
- Go-live formalities (SDK publishing, legal terms, secret rolls) are deferred until the user says go live; don't start these. See `docs/decisions/go-live.md`.

## .NET

- Target `net11.0` using the exact SDK in `global.json`. Use source-generated `System.Text.Json` metadata and register every request/response type in its serializer context.
- Direct Newtonsoft references and authored API usage are banned. Only Stripe.net's transitive Newtonsoft.Json dependency is allowed in dashboard projects, and Microsoft.EntityFrameworkCore.Design's (`PrivateAssets="all"`) in Entity Framework Core design hosts: projects named `*.EntityFrameworkDesign` that are never packed, published or referenced and only run `dotnet ef`. The package-graph guard enforces both. Preserve `BannedSymbols.txt`, RS0030 enforcement and the package-graph guard in `Directory.Build.targets`. New banned APIs need a reason and replacement.
- Every HTTP endpoint, including health, admin and examples, uses `TerraScale.Endpoints` via ProjectReference: one endpoint class per file; register with `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. No raw minimal API handlers or controllers. Exceptions: gRPC, Prometheus scraping and OpenAPI/Scalar documents.
- Public HTTP APIs need OpenAPI rendered by Scalar.
- Manage versions only in `Directory.Packages.props`; no Version on PackageReference. Prerelease packages only where .NET 11 requires them. Commit lock files; restore locked. `NuGet.config` uses nuget.org; keep source mappings correct if a source is added.
- Keep project files minimal. Use ProjectReference for libraries not consumed as packages.
- Warnings are errors in every configuration. Never lower warning levels or weaken analyzer/style enforcement. `NoWarn` is limited to existing justified entries in shared build files. No global suppressions, assembly suppressions, enforced-rule severity reductions or warning pragmas outside generated code. Any necessary local suppression needs a documented justification and human approval.

## Tests

- Libs Fast must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug.
- Use TUnit and Bogus. Service tests are integration/end-to-end only. Run TUnit through `dotnet run --project` (the Makefile does this).
- Tests must be safe alongside every other test. Keep default parallelism; no `NotInParallel`, `ParallelLimiter`, sequential execution or lower parallelism to hide isolation bugs. Servers may be shared per assembly or in a small pool; each test owns its database, tenant or prefix and synthetic data, then cleans up. Server fixtures own their ephemeral ports, directories and lifecycle. No ordering dependencies, mutable static state outside fixture-owned resources, fixed ports or wall-clock sleeps.
- Auth and TerraBase each have Fast and Slow integration suites plus source-linked TestHarness utilities; the harness is not another suite. Library tests stay in `tests/TerraScale.Endpoints.Tests` and `tests/TerraScale.Logging.Tests`.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. New features add at most one happy-path and one critical-failure test. Never skip, disable or weaken retained tests to bypass failures.
- No workflow runs the Slow suites: run them locally with `make test-slow` or an area's `*-test-slow` target.

## CI

- `.github/workflows/ci.yml` is the only pull-request workflow. Its `changes` job picks the legs from the changed paths, and `all-green`, the one required check, needs every leg: a failed or cancelled leg fails it, a skipped leg passes. "Re-run failed jobs" re-runs `all-green` in the same run.
- Releases stay in jobs defined in `ci.yml` (TerraBase, ts-auth) and `images-ghcr.yml` (dashboard): ts-orch accepts release tokens only from those two files, never from a called workflow.
- Site deploys run in `.github/workflows/sites.yml` (`Sites`), never in `ci.yml`, so a site deploy cannot turn a `CI` run red. A main push selects each site whose paths its own `before..sha` diff changed. `deploy-landing` and `deploy-status` wait for their site's lock (`landing-deploy`, `status-deploy`), then check out the tip of `main`, build and deploy it, and prove the live revision equals the commit they checked out with one check, no retry loop; `deploy-docs` calls `docs-deploy.yml`, which skips a revision that is no longer the head of `main`. Retry a failed site deploy with a full re-run of its `Sites` run (`gh run rerun <run id>`, never `--failed`); a `Sites` dispatch on `main` redeploys landing and status, and docs only when the tip of `main` passes `docs-site/scripts/check-main.py` (`docs/dev/ci-recovery.md`).
