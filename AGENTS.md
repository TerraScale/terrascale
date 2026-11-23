<!-- shared-rules:start -->

## Shared TerraScale rules

### Agent rules

- **Never edit any `AGENTS.md` without the user's explicit consent.**
- **Never create `.md` files unless the user explicitly asks for them first.** Prefer updating existing documentation or answering in chat instead.

### Shipping code

- Ship every change as a pull request to `main`. Fetch `origin` first and branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution. Ignore outside contributors' PRs unless asked.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Before pushing, build or type-check only the project you changed. GitHub CI on the PR is the gate. A PR squash-merges as soon as its checks are green and its review passed; rebase it when it conflicts. Never push directly to, force-push or delete `main`.
- Deploy only green `main`. If your merge makes `main` red, fix or revert within about 30 minutes. For a red `main` you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to `mariogk-desktop`.
- `StorageFormat.cs` bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Code

- **No comments:** no `//`, `#`, `/* */`, `///` doc comments or commented-out code in code, scripts or build files, including code you move or rewrite. Names and types carry the meaning; non-obvious choices go in the commit message as `DECISION:`.
- **Fix the root cause:** no workarounds — no retries, sleeps, longer timeouts, swallowed errors, special cases, flags or skipped or weakened journey steps that hide a symptom. If the cause is outside your task or outside our code, report it instead of patching around it.
- **No legacy:** no deprecated paths, compatibility shims, fallbacks, old-format readers, dead code or copies kept "for now". Replace the old path and delete it in the same change.
- **No tests but the journey:** the only test is the user-journey E2E suite in `terrascale` (runs on every `terrascale` PR against a throwaway environment and after every release against production). Never add unit, integration, contract, Fast or Slow tests, test projects, fakes or fixtures in any repo; delete any you find. A behaviour that matters becomes a journey step. Never skip, weaken or retry a journey test; the only allowed way to stop a flaky test from blocking PRs is quarantine (see `terrascale/e2e/AGENTS.md` "Quarantine": an open issue titled `Flaky journey test: <test>` with label `journey-quarantine` skips it on PRs while `main` still runs and reports it; closing the issue after the real fix ends quarantine). Production monitors (`ts-orchestrator` `multi-region`, `deploy-lag`, `app-canary`) are not tests; keep them. `ts-orchestrator` also keeps its single-host E2E (job `e2e-m1`) as its PR gate.

### CI and proof

- One PR workflow per repo (`ci.yml`); its last job is `all-green`, the one required check. Release, deploy and monitor workflows stay separate.
- Keep CI fast: no test jobs, no duplicate builds, cached toolchains, jobs in parallel.
- The shared CI-time check (`ci-time.yml` in `TerraScale/.github`) runs after every CI run and compares working time (the longest chain of runner execution time, excluding queue wait). A run is slow when working time exceeds the median of the last 5 successful runs with the same jobs by more than 30 s, or a job exceeds the slowest of its own earlier runs by more than 30 s. An issue opens when the same slow signal repeats on the previous run with the same jobs; it never blocks a merge — investigate and fix it like a bug.
- **UI changes:** screenshot every changed page before (`origin/main`) and after (your branch), using the same viewport, data and theme, in every supported theme. Save PNGs outside the repo as `~/.local/state/shots/<branch>/<page>-before.png` and `-after.png`; never commit screenshots. Embed each as a Markdown image with its absolute path (e.g. `![after: /settings](/home/<user>/.local/state/shots/<branch>/settings-after.png)`) in your final reply and list the pages in the PR body.

### Secrets and hard gates

- Runtime secrets: OCI Vault in `gru` via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a `0600` file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- `ts-auth` and `ts-dashboard` store data in TerraBase through `ts-dotnet`; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in CI or helpers. Live `ts-status` API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

<!-- shared-rules:end -->

# TerraScale monorepo

## Development skills

Step-by-step guides live in `.agents/skills/`:

- `add-feature`: build a feature end to end (owner, contract, storage, SDKs, UI, docs, journey).
- `add-endpoint`: add an HTTP endpoint with `TerraScale.Endpoints`.
- `sdk-parity`: add or change an operation across the five SDKs.
- `simple-ui`: design and build dashboard, auth and site screens.
- `landing-page-design`: landing page design guide.

## Layout and commands

- Libraries: `src/TerraScale.Analyzers`, `src/TerraScale.Endpoints*` and `src/TerraScale.Logging`. Local dev host: `src/TerraScale.Aspire` (`make dev`). Read `src/TerraBase/AGENTS.md`, `src/TerraScale.Authentication/AGENTS.md` and `src/TerraScale.Dashboard/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, CI and repo helper scripts in `.github/scripts/`, and native storage build scripts and pin (`turso-pin.env`) in `src/vendor/Turso.Native/`. Public Cloudflare Workers sites live under `sites/` (`docs`, `landing`, `status`, `atlas`). All benchmark code and reports belong in `benchmark/` (full-E2E C#/Go/Rust suite `ts-bench`, .NET microbenchmarks in `benchmark/dotnet/`, saved Markdown tables in `benchmark/bench/`).
- Use the root Makefile when a target exists. Build: `make build`; full checks: `make check`; local stack: `make dev`; E2E benchmarks: `make bench`. Area targets: `libs-build`, `libs-check`, `auth-build`, `auth-check`, `terrabase-build`, `dashboard-build`, `dashboard-lint`, `dashboard-check`, `aspire-build`, `benchmark-build`, `benchmark-check`, `docs-build`, `landing-build`, `status-build`, `atlas-build`.
- SDKs live in `sdk/dotnet`, `sdk/js`, `sdk/go`, `sdk/python` and `sdk/rust`. `make sdk-dotnet-check`, `make sdk-js-check`, `make sdk-go-check`, `make sdk-python-check` or `make sdk-rust-check` installs or restores one SDK locked and builds or type-checks it; `make sdk-check` runs all five.
- Services consume shared libraries and `sdk/dotnet` directly through relative `ProjectReference` entries; `TerraScale.slnx` is the single solution file.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- Never fork the Turso database: use upstream `tursodatabase/turso` at a pinned commit, tarball verified by SHA-256 at build time (`src/vendor/Turso.Native/turso-pin.env`). Only the Turso client may be forked (today: the vendored driver in `src/vendor/Turso.Data.Sqlite`), and only when upstream cannot do what we need.
- Every feature is always on. No feature flags, opt-in settings, enabled booleans, deploy toggles or kill switches; delete guarded paths. Values have one correct setting in code; expose no tuning knobs. No fakes, demo stores or skip-auth: the journey stands in only for third parties (`e2e/AGENTS.md`).
- Derive region, node and peers from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE` (one `<node> <region> <netbird-ipv4>` line per peer). Every region serves automatically. Libraries must preserve automatic discovery for services and benchmarks.
- CI rejects switch-style configuration; unavoidable third-party names need a narrow allowance and one-line reason. Always-on behavior preserves authentication, consent, permissions, revocation, validation, isolation, backups and recovery safeguards. Preserve CI branch/event identity, quality gates and cleanup conditions.
- Greenfield: no old-format shims, compatibility layers or migrations unless requested. Public HTTP APIs use `/v1`; breaking API changes are explicit compatibility decisions. Generated IDs are ULIDs.
- Application data lives in TerraBase databases. Platform services store it in relational tables through the TerraBase EF Core provider (`sdk/dotnet/src/TerraScale.EntityFrameworkCore`): one `DbContext` per service, typed columns, keys, foreign keys and constraints for invariants, and committed migrations applied at startup. Never edit, rename or delete a merged migration; add a new one (`.github/scripts/ci/migrations-frozen.py` fails the PR). No alternate database, local-file or memory-only application store. TerraBase external persistence uses Tigris. `sites/status/` and `sites/atlas/` retain their Cloudflare D1/Durable Object availability exceptions.
- Each product owns its data: `ts-auth` owns identity/access, organizations, projects and API credentials; TerraBase owns databases, documents, queries, transactions, backups, indexes, schema and usage metering; Dashboard owns platform UI/API/MCP, plans, subscriptions, billing and spending caps; `ts-orch` owns placement, scaling and rollout. Services call other products through their APIs (the dashboard calls TerraBase over MagicOnion through the shared contracts library); never read another product's storage. TerraBase validates `ts-auth` tokens offline.
- Prefer mature maintained packages and simple code. Add abstractions only for real boundaries or meaningful duplication. Verify APIs, dependencies and claimed guarantees against implementation and integration evidence. Use mutually compatible current versions and reproducible builds.
- UI work follows `.agents/skills/simple-ui/SKILL.md`: simple purpose, architecture, design and implementation; no taglines, helper text or secondary labels. If a screen needs them, fix the layout.
- Update public docs in `sites/docs/` with behavior changes. Workflow changes go through the CI owner.
- Go-live formalities (SDK publishing, legal terms, secret rolls) are deferred until the user says go live; do not start them.

## .NET

- Target `net11.0` using the exact SDK in `global.json`. Use source-generated `System.Text.Json` metadata and register every request/response type in its serializer context.
- Direct Newtonsoft references and authored API usage are banned. Only Stripe.net's transitive `Newtonsoft.Json` dependency is allowed in dashboard projects, `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets="all"`) in EF Core design hosts (`*.EntityFrameworkDesign`), and `Aspire.Hosting` in `TerraScale.Aspire`; neither design hosts nor `TerraScale.Aspire` are packed, published or referenced. Preserve `BannedSymbols.txt`, RS0030 enforcement and the package-graph guard in `Directory.Build.targets`. New banned APIs need a reason and replacement.
- Every HTTP endpoint (including health, admin and examples) uses `TerraScale.Endpoints` via `ProjectReference`: one endpoint class per file; register with `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. No raw minimal API handlers or controllers. Exceptions: gRPC, Prometheus scraping and OpenAPI/Scalar documents.
- Public HTTP APIs need OpenAPI rendered by Scalar.
- Manage versions only in `Directory.Packages.props` (no `Version` on `PackageReference`). Prerelease packages only where .NET 11 requires them. Commit lock files; restore locked. `NuGet.config` uses nuget.org; keep source mappings correct if a source is added.
- `Microsoft.CodeAnalysis.*` stays at or below the compiler of the SDK pinned in `global.json`, or analyzers fail to load (CS8032).
- `System.Formats.Cbor` is pinned centrally only because Fido2 4.3.2 asks for 9.0.0 (GHSA-vmv3-wvjp-wqpj); drop the pin once Fido2 asks for a patched version.
- Keep project files minimal; use `ProjectReference` for libraries not consumed as packages.
- Warnings are errors in every configuration. Never lower warning levels or weaken analyzer/style enforcement. `NoWarn` is limited to existing justified entries in shared build files. No global suppressions, assembly suppressions, enforced-rule severity reductions or warning pragmas outside generated code; any necessary local suppression needs a documented justification and human approval.

## SDKs

- `sdk/dotnet`: Deploy order invariant — every `main` push touching `sdk/dotnet/**` auto-releases `ts-auth` and `TerraScale.Dashboard`, while TerraBase deploys only through manual `build-only` + `release-only` dispatches; any SDK or EF Core change depending on a new TerraBase server feature must land only after production TerraBase is running it. Keep public APIs async, cancellation-aware, with XML docs where `CS1591` is enforced; never dispose a caller-owned `HttpClient`; verify packaging with `bash sdk/dotnet/scripts/check-release.sh`.
- `sdk/go`: Module `terrascale.tech/sdk` targeting the single global origin (`https://api.terrascale.tech` and `https://auth.terrascale.tech`); no client-side regional discovery or failover.
- `sdk/js`: `@terrascale/sdk` uses JavaScript ES modules with JSDoc in `src/` emitting `types/` (do not rewrite to TypeScript); keep credentials and server-only operations out of browser entrypoints; verify `npm run check:v1-route-manifest`.
- `sdk/python`: PyPI `terrascale`; keep sync (`client.py`) and async (`aio.py`) clients identical via `_spec.py`; verify `ruff check src` and `mypy`.
- `sdk/rust`: Single `terrascale` crate preserving `Cargo.lock`; verify `cargo fmt --all -- --check` and `cargo clippy --locked --all-targets --all-features -- -D warnings`; keep Postgres support behind the `postgres` Cargo feature.

## Sites

- `sites/landing`: SvelteKit with `@sveltejs/adapter-static` targeting `https://terrascale.tech`; strict prerendering only (no SPA fallback, runtime SSR or API handlers without human approval). Owns the canonical `sites/landing/static/{pricing,regions}.json` consumed by `sites/docs`, `sites/status`, `TerraBase` and `TerraScale.Dashboard`. Regenerate derived assets with `npm run icons:sync` and `npm run world-map:sync`.
- `sites/docs`: VitePress site in `sites/docs/docs/`; run `npm run canonical:sync` to regenerate pricing, LLM and i18n pages from `sites/landing/static/{pricing,regions}.json`, and `npm run check:links`. Never publish private database names, internal hostnames, topology or credentials, and never claim unshipped behavior.
- `sites/status`: Cloudflare Worker serving server-rendered HTML with plain CSS + Lightning CSS; retains its D1 + Durable Objects availability exception (never depend on TerraBase) and discovers regions dynamically from `ts-orch`.
- `sites/atlas`: Cloudflare Worker (Vite Plus) backed by D1 (`ATLAS_DB`), a single Durable Object (`AtlasStore`) and KV (`ATLAS_KV`); retains its availability exception (never depend on TerraBase), stores per-`(project, database)` placements with versioned tombstones, and has no deploy automation.

## Tests

- The journey in `e2e/` is the only test. It drives TerraBase, `ts-auth`, the dashboard built from the commit under test, and all five SDKs on every PR (`journey` job of `ci.yml`, `.github/workflows/journey.yml`), and against production after every release and daily (`.github/workflows/production-journey.yml`, through `e2e/env/production.sh` only). Read `e2e/AGENTS.md` before changing it.
- A behaviour that matters becomes a journey step in `e2e/`. Never add any other test, test project, fake or fixture.
- `checks` fails a PR that adds a file outside `e2e/`, or moves one in, whose folder or name marks it as a test (pattern in the `No test file outside the journey` step of `ci.yml`).

## CI

- **PR gate (`ci.yml`):** the only PR workflow. `all-green`, its last job, is the single required check (failed/cancelled fails it; skipped passes; "Re-run failed jobs" re-runs `all-green` in the same run).
- **PR legs:** `changes` selects legs from changed paths (each filter lists only files its job reads); `checks` runs policy checks (legacy-name scan, workflow lint, frozen EF Core migrations, storage train, retired-provider grep, no added test file). Legs build and check the GitHub merge commit with no unit tests:
  - `libs` and `auth`: locked restore, whitespace format, Release build with analyzers/warnings-as-errors (`libs` also checks style; `auth` checks NuGet audit and OpenAPI contract).
  - `terrabase`: restore, whitespace check, pinned native libraries, and linux-arm64 single-file release publish (fails if a native library lies beside the file).
  - `dashboard` (inlined in `ci.yml`): Release build with analyzers/warnings-as-errors, then lint (whitespace format only).
  - `aspire`: locked restore, whitespace format and Release build of `src/TerraScale.Aspire`.
  - `sdk`: `make sdk-<x>-check` for each changed SDK (`sdk-release.yml` and `.github/scripts/sdk-release.py` select none).
  - `docs`, `landing`, `status`: `docs` checks generated pages against `sites/landing/static/{pricing,regions}.json` and builds offline; `landing` and `status` build theirs.
  - `journey`: runs unless every changed file is ignored by it (Markdown, sites, other workflows, `benchmark/`, unused scripts).
- **Main qualification and releases:** `main` push CI runs the journey for every commit. Deploy targets require the latest `main`-push CI to pass `all-green` and both journey shards (a PR run or dispatch cannot replace it). `qualified-main` keeps exact-commit qualification for manual TerraBase releases and SDK tags; `build-only` and `release-only` maintenance reuse that proof, while native-artifact maintenance does not qualify a release. TerraBase reaches regions only through a `release-only` dispatch. Release jobs use the newest published `ts-orchestrator` `ci-` release with `qualification.json` and verify its binary hash.
- **Auth and dashboard releases:** stay in `ci.yml` and `images-ghcr.yml` (OIDC permissions require a `main` push or dispatch there). `Sites` requests a `main` dispatch only when the reconciler finds work due; the dispatched job re-selects its target under its release lock (never deploying the dispatch SHA merely because it triggered the run), reuses or builds/checks the exact target image, and moves `:main` only inside that lock after the deploy decision.
- **SDK releases (`sdk-release.yml`):** after each green `main`-push `CI` run, releases the Go CLI when `sdk/go`, `.github/scripts/sdk-release.py` or `sdk-release.yml` changed since the newest `sdk/go/v0.0.X` tag. Push an SDK tag (`sdk/<js|python|rust|dotnet>/v0.0.X`) on `main` only after `main` CI is green for that exact commit; older or unqualified tags are refused and need a re-run after CI succeeds.
- **Production journey (`production-journey.yml`):** runs after each `CI` or `Images` run that released a service, daily, and on manual dispatch. Auth and dashboard write a `deploy-revision` artifact after proving readiness; manual TerraBase releases use their dispatch commit. Unknown release jobs, missing release steps or missing receipts fail release detection and alert. Reports update one open `Production journey failure` issue under the alert lock; a green journey closes it; runs whose live version changes mid-journey do not update the alert.
- **CI time (`ci-time.yml`):** runs the shared `TerraScale/.github` check after each first-attempt green `CI`, `Sites` (non-scheduled) and `Images (GHCR)` run (never forks). Opens `CI time regression: <workflow> (<event>)` (label `ci-time`) only when the same slow signal repeats on two consecutive runs with the same jobs; never blocks merges.
- **Dependency advisories (`dependency-advisories.yml`):** runs weekly and on dispatch (never on PRs). Scans NuGet packages, pinned `.NET` / `ASP.NET Core` runtime packs (`linux-arm64`, `linux-x64`) and `govulncheck` on `sdk/go`. Opens/updates or closes `Dependency advisories` (label `dependency-advisories`); blocks nothing.
- **Sites reconciliation (`sites.yml`, `.github/scripts/ci/site-deploy-gate.mjs`):** reconciles docs, landing, each status Worker, dashboard and `ts-auth` after every `main` `CI`/`Images` completion, every 15 minutes, and on `main` dispatch.
  - Under each deploy lock, reads live and walks the newest 10 `main` commits to find the first with green `main`-push CI (`all-green` + both journey shards). Auth/dashboard dispatches happen only when `main` head is the target. If none qualifies, on-main live stays live quietly; equal live or live ahead needs no deploy; ancestors advance only when build inputs differ; missing/unreadable/off-main live fails (only a confirmed absent Worker is a first deploy). Every deployed Worker records and proves its target revision at 100% traffic.
  - Each target has one persistent `Deployment state: <thing>` issue (label `deployment-alert`, created by `github-actions[bot]`, never closed by automation) holding `{thing, target, attempts (0–2), notified}`. Every decision and locked job validates state first; bad/duplicate state blocks only that thing; closed issues must be reopened; healthy runs edit only the body.
  - Allows 2 attempts per target, resets for a strict descendant, waits on older/unrelated targets, and records `(target, 0, false)` when live matches. Suppression posts one comment with target, run links and release instructions, then sets `notified: true`. Release suppression by landing a newer qualifying commit or dispatching `Sites` with `reset_thing=<thing>`. Closing the issue does not release suppression. Service dispatches wait for active manual runs and fail after 2 hours.
  - Force reconciliation with `gh workflow run sites.yml --ref main` (or `images-ghcr.yml` for dashboard alone, `ci.yml -f reconcile_auth=true` for auth alone). Undo the rule by reverting its merge commit (workflows and scripts together) via PR, then manually closing the state issues.
