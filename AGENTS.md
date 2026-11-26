<!-- shared-rules:start -->

## Shared TerraScale rules

### Agent rules

- **Never edit any `AGENTS.md` without the user's explicit consent.**
- **Never create `.md` files unless the user explicitly asks for them first.** Prefer updating existing documentation or answering in chat instead.

### Shipping code

- Ship every change as a pull request to `main`. Fetch `origin` first and branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution. Ignore outside contributors' PRs unless asked.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Verify before you open a PR: build or type-check the projects you changed, run the affected `e2e/` journey lanes on your machine (in `terrascale`), and get a clean pre-PR council review of the diff. PR CI only proves the change compiles. A PR squash-merges as soon as `all-green` is green and its review passed; rebase it when it conflicts. Never push directly to, force-push or delete `main`.
- Every merge to `main` builds and deploys at once. If your merge breaks production or makes `main` red, fix or revert within about 30 minutes. For a red `main` you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to `mariogk-desktop`.
- `StorageFormat.cs` bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Code

- **No comments:** no `//`, `#`, `/* */`, `///` doc comments or commented-out code in code, scripts or build files, including code you move or rewrite. Names and types carry the meaning; non-obvious choices go in the commit message as `DECISION:`.
- **Fix the root cause:** no workarounds — no retries, sleeps, longer timeouts, swallowed errors, special cases, flags or skipped or weakened journey steps that hide a symptom. If the cause is outside your task or outside our code, report it instead of patching around it.
- **No legacy:** no deprecated paths, compatibility shims, fallbacks, old-format readers, dead code or copies kept "for now". Replace the old path and delete it in the same change.
- **No tests but the journey:** the only test is the user-journey E2E suite in `terrascale` (run locally before every `terrascale` PR against a throwaway environment, and asynchronously after every release against production). Never add unit, integration, contract, Fast or Slow tests, test projects, fakes or fixtures in any repo; delete any you find. A behaviour that matters becomes a journey step. Never skip, weaken or retry a journey test; the only allowed way to set a flaky test aside is quarantine (see `e2e/AGENTS.md` "Quarantine": an open issue titled `Flaky journey test: <test>` with label `journey-quarantine`; the production journey still runs and reports it; closing the issue after the real fix ends quarantine). Production monitors (`ts-orchestrator` `multi-region`, `deploy-lag`, `app-canary`) are not tests; keep them. `ts-orchestrator` also keeps its single-host E2E (job `e2e-m1`) as its PR gate.

### CI and proof

- One PR workflow per repo (`ci.yml`); its last job is `all-green`, the one required check. Release, deploy and monitor workflows stay separate. Attestation, monitors and the production journey run asynchronously and never delay a deploy.
- Keep CI fast: no test, lint or policy jobs in `terrascale` CI, no duplicate builds, cached toolchains, jobs in parallel. Targets: PR CI under 3 minutes, merge to live in every region under 5 minutes.
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

- Shared libraries include `src/TerraScale.Endpoints*`, `src/TerraScale.Logging` and `src/TerraScale.QuicRpc`. The local dev host is `src/TerraScale.Aspire` (`make dev`). Read `src/TerraBase/AGENTS.md`, `src/TerraScale.Authentication/AGENTS.md` and `src/TerraScale.Dashboard/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, CI and repo helper scripts in `.github/scripts/`, and native build inputs in `native/`. Public Cloudflare Workers sites live under `sites/` (`docs`, `landing`, `status`). Benchmarks live in `benchmark/`, including the C#/Go/Rust E2E suite `ts-bench`, shared-library microbenchmarks in `benchmark/dotnet/` and reports in `benchmark/bench/`.
- Use the root Makefile when a target exists. Build with `make build`, check with `make check`, start the local stack with `make dev`, and run E2E benchmarks with `make bench`. Area targets include `libs-build`, `libs-check`, `auth-build`, `auth-check`, `terrabase-build`, `platform-build`, `dashboard-build`, `dashboard-lint`, `dashboard-check`, `aspire-build`, `benchmark-build`, `benchmark-check`, `docs-build`, `landing-build` and `status-build`.
- SDKs live in `sdk/dotnet`, `sdk/js`, `sdk/go`, `sdk/python` and `sdk/rust`. `make sdk-dotnet-check`, `make sdk-js-check`, `make sdk-go-check`, `make sdk-python-check` or `make sdk-rust-check` runs the build or static checks for the selected SDK; `make sdk-check` runs all five.
- Services consume shared libraries and `sdk/dotnet` directly through relative `ProjectReference` entries; `TerraScale.slnx` is the monorepo solution.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- TerraBase uses upstream SQLite3MultipleCiphers 2.7.1 with SQLite 3.54.1 through `src/TerraBase.Storage/SqliteNative.cs`. `native/native-pin.env` pins the source tarball and SHA-256, and `native/build-native.sh` verifies the hash before building it.
- Every feature is always on. No feature flags, opt-in settings, enabled booleans, deploy toggles or kill switches; delete guarded paths. Values have one correct setting in code; expose no tuning knobs. No fakes, demo stores or skip-auth: the journey stands in only for third parties (`e2e/AGENTS.md`).
- Derive region, node and peers from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE`. Every region holding a database serves it. Libraries must preserve automatic discovery for services and benchmarks.
- CI rejects switch-style configuration; unavoidable third-party names need a narrow allowance and one-line reason. Always-on behavior preserves authentication, consent, permissions, revocation, validation, isolation, backups and recovery safeguards. Preserve CI branch/event identity, quality gates and cleanup conditions.
- Greenfield: no old-format shims, compatibility layers or migrations unless requested. Public HTTP APIs use `/v1`; breaking API changes are explicit compatibility decisions. Generated IDs are ULIDs.
- Use TerraBase v2 as the application data store. Platform services use Dapper.AOT over the TerraBase .NET SDK and apply embedded DbUp SQL migrations at startup. Use typed columns, keys, foreign keys and constraints to enforce invariants. Add a new script for each schema change and keep previously merged scripts immutable. PR #2329 removed the former ORM provider from the .NET SDK. TerraBase archives backups to OCI Object Storage. `sites/status/` retains its Cloudflare D1 and Durable Objects availability exception.
- Each product owns its data. `ts-auth` owns identity, access, organizations, projects and API credentials. TerraBase owns database storage, queries, transactions, replication and backup recovery. `ts-platform` serves platform APIs such as files, shapes and subscription usage. Dashboard owns the platform UI, MCP, plans, subscriptions, billing and spending caps. `ts-orch` owns placement, scaling and rollout. Services use each other's APIs. TerraBase validates `ts-auth` tokens offline.
- Prefer mature maintained packages and simple code. Add abstractions only for real boundaries or meaningful duplication. Verify APIs, dependencies and claimed guarantees against implementation and integration evidence. Use mutually compatible current versions and reproducible builds.
- UI work follows `.agents/skills/simple-ui/SKILL.md`: simple purpose, architecture, design and implementation; no taglines, helper text or secondary labels. If a screen needs them, fix the layout.
- Update public docs in `sites/docs/` with behavior changes. Workflow changes go through the CI owner.
- Go-live formalities (SDK publishing, legal terms, secret rolls) are deferred until the user says go live; do not start them.

## .NET

- Target `net11.0` using the exact SDK in `global.json`. Use source-generated `System.Text.Json` metadata and register every request/response type in its serializer context.
- Use `System.Text.Json` in authored code. Direct Newtonsoft package references are banned. The package guard allows transitive Newtonsoft through Stripe.net in Dashboard and through Aspire.Hosting in the local development host. `TerraScale.Aspire` stays outside the package, publish and project-reference graphs. Preserve `BannedSymbols.txt`, RS0030 and the package-graph guard in `Directory.Build.targets`. New banned APIs need a reason and replacement.
- Every HTTP endpoint (including health, admin and examples) uses `TerraScale.Endpoints` via `ProjectReference`. Use one endpoint class per file and register with `AddTerraScaleEndpoints()`, `MapTerraScaleEndpoints()` and `MapTerraScaleOpenApi()`. Keep raw minimal API handlers and controllers out. Prometheus scraping and OpenAPI/Scalar documents are exceptions.
- Public HTTP APIs need OpenAPI rendered by Scalar.
- Manage versions only in `Directory.Packages.props` (no `Version` on `PackageReference`). Prerelease packages only where .NET 11 requires them. Commit lock files; restore locked. `NuGet.config` uses nuget.org; keep source mappings correct if a source is added.
- `Microsoft.CodeAnalysis.*` stays at or below the compiler of the SDK pinned in `global.json`, or analyzers fail to load (CS8032).
- Keep project files minimal; use `ProjectReference` for libraries not consumed as packages.
- Warnings are errors in every configuration. Never lower warning levels or weaken analyzer/style enforcement. `NoWarn` is limited to existing justified entries in shared build files. No global suppressions, assembly suppressions, enforced-rule severity reductions or warning pragmas outside generated code; any necessary local suppression needs a documented justification and human approval.

## SDKs

- `sdk/dotnet` is built into `ts-auth` and Dashboard. Land server changes first and verify the TerraBase or `ts-platform` rollout before merging dependent SDK or service changes. Applicable `main` pushes automatically deploy TerraBase and `ts-platform`. Auth and Dashboard reconcile on `main` pushes and deploy when qualified inputs change. Keep public APIs async and cancellation-aware, add XML docs where `CS1591` is enforced, preserve caller-owned `HttpClient` instances, and verify packaging with `bash sdk/dotnet/scripts/check-release.sh`.
- `sdk/go`: Module `terrascale.tech/sdk` targeting the single global origin (`https://api.terrascale.tech` and `https://auth.terrascale.tech`); no client-side regional discovery or failover.
- `sdk/js`: `@terrascale/sdk` uses JavaScript ES modules with JSDoc in `src/` emitting `types/` (do not rewrite to TypeScript); keep credentials and server-only operations out of browser entrypoints; verify `npm run check:v1-route-manifest`.
- `sdk/python`: PyPI `terrascale`; keep sync (`client.py`) and async (`aio.py`) clients identical via `_spec.py`; verify `ruff check src` and `mypy`.
- `sdk/rust`: Single `terrascale` crate preserving `Cargo.lock`; verify `cargo fmt --all -- --check` and `cargo clippy --locked --all-targets --all-features -- -D warnings`; keep Postgres support behind the `postgres` Cargo feature.

## Sites

- `sites/landing`: SvelteKit with `@sveltejs/adapter-static` targeting `https://terrascale.tech`; strict prerendering only (no SPA fallback, runtime SSR or API handlers without human approval). Owns the canonical `sites/landing/static/{pricing,regions}.json` consumed by `sites/docs`, `sites/status`, `TerraBase` and `TerraScale.Dashboard`. Regenerate derived assets with `npm run icons:sync` and `npm run world-map:sync`.
- `sites/docs`: VitePress site in `sites/docs/docs/`; run `npm run canonical:sync` to regenerate pricing, LLM and i18n pages from `sites/landing/static/{pricing,regions}.json`, and `npm run check:links`. Never publish private database names, internal hostnames, topology or credentials, and never claim unshipped behavior.
- `sites/status`: Cloudflare Worker serving server-rendered HTML with plain CSS + Lightning CSS; retains its D1 + Durable Objects availability exception (never depend on TerraBase) and discovers regions dynamically from `ts-orch`.

## Tests

- The journey in `e2e/` is the only test. It drives TerraBase, `ts-platform`, `ts-auth`, Dashboard and all five SDKs against builds from the commit under test. No CI job runs it on a PR or a `main` push: run the lanes your change affects locally (`e2e/AGENTS.md`, "Run it on your machine") before opening the PR and cite the result for the pre-PR council review. `.github/workflows/production-journey.yml` runs it against production after each release and daily through `e2e/env/production.sh`. Read `e2e/AGENTS.md` before changing it.
- A behaviour that matters becomes a journey step in `e2e/`. Never add any other test, test project, fake or fixture. The pre-PR council review rejects a PR that adds a file outside `e2e/`, or moves one in, whose folder or name marks it as a test, and checks repository policy (storage-format review, legacy terms, retired hosting provider names, workflow lint) that CI no longer runs.

## CI

- **PR gate (`ci.yml`):** the only PR workflow; it only compiles. `changes` selects legs from changed paths and `all-green`, its last job, is the single required check (failed/cancelled fails it; skipped passes; "Re-run failed jobs" re-runs `all-green` in the same run). No test, journey, lint, format, audit or policy job runs. Every `setup-dotnet` caches NuGet packages from `**/packages.lock.json` and every `setup-node` caches npm. Legs build the GitHub merge commit:
  - `libs`, `auth`, `aspire`: locked restore and Release build with analyzers/warnings-as-errors.
  - `terrabase`: pinned native libraries and Release builds of TerraBase and `ts-platform` on linux-arm64.
  - `dashboard`: locked restore and `npm ci` in parallel, then the Release build, which bundles the web assets.
  - `sdk`: `make sdk-<x>-check` for each changed SDK (`sdk-release.yml` and `.github/scripts/sdk-release.py` select none).
  - `docs`, `landing`, `status`: `docs` checks generated pages against `sites/landing/static/{pricing,regions}.json` and builds offline; `landing` builds; `status` type-checks and builds.
  - `workflow_dispatch` with `pr_gate` runs every leg on any ref without publication.
- **`main` pushes (`ci.yml`): build and deploy only.** `changes` selects the services whose inputs changed and their deploy jobs run in parallel: `main` (TerraBase), `main-platform` (`ts-platform`) and `main-auth` (`ts-auth`). No PR leg runs again. A commit is deployable as soon as it is on `main`. Each job takes the `production-<service>` lock, skips when `.github/scripts/ci/check-live-ahead.sh` (or, for auth, `site-deploy-gate.mjs`) finds live at or ahead of the commit, publishes the linux/arm64 chiseled image to `sa-saopaulo-1` OCIR, verifies the arm64 child, mirrors it to every OCIR region with `.github/scripts/ci/publish-ocir.sh` and deploys it through `.github/scripts/ci/deploy-v2.sh`. Every published image uploads an `attest-targets-<service>` artifact (`{service, image, arm64, revision, tags}`). `all-green` passes when `changes` and every selected deploy job succeed. Budget: CI under 3 minutes, every region live within 5 minutes of the merge.
- **Maintenance dispatches.** `publication=build-only` publishes and mirrors the TerraBase image with only the revision tag and no release; `publication=release-only` deploys a pinned TerraBase arm64 digest (`terrabase_arm64`) of the same `main` revision after verifying its label and its `attest-images.yml` provenance and SBOM attestations. Both require a completed `main`-push CI run for the revision (`.github/actions/qualified-main`).
- **Dashboard releases (`images-ghcr.yml`, `Images (OCIR)`).** Runs on every `main` push and on dispatch under the `dashboard-release` lock. `site-deploy-gate.mjs dashboard` reconciles live with the commit and skips in seconds when the dashboard inputs did not change. Otherwise it builds the image (restore and `npm ci` in parallel, no lint), mirrors it to every OCIR region, deploys through `deploy-v2.sh`, proves the live revision, and uploads `deploy-revision` and `attest-targets-dashboard`.
- **Attestation (`attest-images.yml`).** Runs after each `main` `CI` or `Images (OCIR)` run completes and reads its `attest-targets-*` artifacts; a run without any exits in seconds. For each image it generates build provenance and an SPDX SBOM, attests both, attaches the Sigstore bundles to every OCIR region with `.github/scripts/ci/attach-image-bundles.sh` and verifies them. It never delays a deploy and cannot fail `CI` or trigger `revert-red-main.yml`. `.github/scripts/ci/prove-image-transport.sh` proves bundle-free mirroring, bundle attachment and release ordering against a local `registry` on port 5000 with `oras`; run it before changing the publication scripts.
- **SDK releases (`sdk-release.yml`):** after each green `main`-push `CI` run, releases the Go CLI when `sdk/go`, `.github/scripts/sdk-release.py` or `sdk-release.yml` changed since the newest `sdk/go/v0.0.X` tag. Push an SDK tag (`sdk/<js|python|rust|dotnet>/v0.0.X`) on `main` only after `main` CI is green for that exact commit; older or unqualified tags are refused and need a re-run after CI succeeds.
- **Production journey (`production-journey.yml`).** Runs asynchronously after a successful deploy step (`main`, `main-platform`, `main-auth`, `release-only` or `Publish and release`) in `CI` or `Images (OCIR)`, daily, and on manual dispatch with the exact `suite_sha`. TerraBase release detection records the workflow's source SHA. Auth and Dashboard supply a `deploy-revision` artifact. Reports update the open `Production journey failure` issue under the alert lock. A green journey closes it. Runs whose live version changes mid-journey skip the alert update.
- **CI time (`ci-time.yml`):** runs the shared `TerraScale/.github` check after each first-attempt green `CI`, `Sites` (non-scheduled) and `Images (OCIR)` run (never forks). Opens `CI time regression: <workflow> (<event>)` (label `ci-time`) only when the same slow signal repeats on two consecutive runs with the same jobs; never blocks merges.
- **Dependency advisories (`dependency-advisories.yml`):** runs weekly and on dispatch (never on PRs). Scans NuGet packages, pinned `.NET` / `ASP.NET Core` runtime packs (`linux-arm64`, `linux-x64`) and `govulncheck` on `sdk/go`. Opens/updates or closes `Dependency advisories` (label `dependency-advisories`); blocks nothing.
- **Sites reconciliation (`sites.yml`, `.github/scripts/ci/site-deploy-gate.mjs`).** Runs after `CI` or `Images (OCIR)` completes on `main`, every 15 minutes, and on manual `main` dispatch. It reconciles docs, landing and status Workers and can request auth or Dashboard reconciliation.
  - Under each deploy lock, reads live and walks the newest 10 `main` commits to find the first whose `main`-push CI passed `all-green`. Auth/dashboard dispatches happen only when `main` head is the target. If none qualifies, on-main live stays live quietly; equal live or live ahead needs no deploy; ancestors advance only when build inputs differ; missing/unreadable/off-main live fails (only a confirmed absent Worker is a first deploy). Every deployed Worker records and proves its target revision at 100% traffic.
  - Each target has one persistent `Deployment state: <thing>` issue (label `deployment-alert`, created by `github-actions[bot]`, never closed by automation) holding `{thing, target, attempts (0–2), notified}`. Every decision and locked job validates state first; bad/duplicate state blocks only that thing; closed issues must be reopened; healthy runs edit only the body.
  - Allows 2 attempts per target, resets for a strict descendant, waits on older/unrelated targets, and records `(target, 0, false)` when live matches. Suppression posts one comment with target, run links and release instructions, then sets `notified: true`. Release suppression by landing a newer qualifying commit or dispatching `Sites` with `reset_thing=<thing>`. Closing the issue does not release suppression. Service dispatches wait for any active run of that workflow on `main` and fail after 2 hours.
  - Force reconciliation with `gh workflow run sites.yml --ref main` (or `images-ghcr.yml` for dashboard alone, `ci.yml -f reconcile_auth=true` for auth alone). Undo the rule by reverting its merge commit (workflows and scripts together) via PR, then manually closing the state issues.
