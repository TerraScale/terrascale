<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- Ship every change as a pull request to `main`. Fetch `origin` first and branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution. Ignore outside contributors' PRs unless asked.
- Record non-obvious choices in the commit message as `DECISION: <choice>. Reason: <why>`.
- Before pushing, build or type-check only the project you changed. GitHub CI on the PR is the gate. A PR squash-merges as soon as its checks are green and its review passed; rebase it when it conflicts. Never push directly to, force-push or delete `main`.
- Deploy only green main. If your merge makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Never run anything on or SSH to mariogk-desktop.
- StorageFormat.cs bumps and production resets need the user's approval; otherwise put `Needs format bump` in the commit message.

### Code

- No comments: no `//`, `#`, `/* */`, `///` doc comments or commented-out code in code, scripts or build files, including code you move or rewrite. Names and types carry the meaning; non-obvious choices go in the commit message as `DECISION:`.
- Fix the root cause. No workarounds: no retries, sleeps, longer timeouts, swallowed errors, special cases, flags or skipped or weakened journey steps that hide a symptom. If the cause is outside your task or outside our code, report it instead of patching around it.
- No legacy: no deprecated paths, compatibility shims, fallbacks, old-format readers, dead code or copies kept "for now". Replace the old path and delete it in the same change.
- No tests but the journey. The only test is the user-journey E2E suite in terrascale: it runs on every terrascale PR against a throwaway environment and after every release against production. Never add unit, integration, contract, Fast or Slow tests, test projects, fakes or fixtures in any repo; delete any you find. A behaviour that matters becomes a journey step. Production monitors (ts-orchestrator `multi-region`, `deploy-lag`, `app-canary`) are not tests; keep them. ts-orchestrator also keeps its single-host E2E (job `e2e-m1`) as its PR gate.

### CI and proof

- One PR workflow per repo (`ci.yml`); its last job is `all-green`, the one required check. Release, deploy and monitor workflows stay separate.
- CI must be as fast as it can be: no test jobs, no duplicate builds, cached toolchains, jobs in parallel.
- The shared CI-time check (`ci-time.yml` in `TerraScale/.github`) runs after every CI run. It compares working time, the longest chain of time a runner spent on the jobs, not time spent waiting for a runner. A run is slow when that time is more than 30 s over the median of the last 5 successful runs with the same jobs, or a job is more than 30 s over the slowest of its own earlier runs. An issue opens when the same slow job, or the slow working time, repeats on the previous run with the same jobs, and never blocks a merge; investigate it and fix the cause like a bug.
- UI changes: screenshot every changed page before (`origin/main`) and after (your branch), same viewport, data and theme, in every supported theme. Save the PNGs outside the repo as `~/.local/state/shots/<branch>/<page>-before.png` and `-after.png`; never commit screenshots. Show them in your T3 thread: if you have T3 tools, `html_render` a before/after grid (absolute local image paths are inlined); otherwise embed each as a Markdown image with its absolute path, e.g. `![after: /settings](/home/<user>/.local/state/shots/<branch>/settings-after.png)`, in your final reply. List the pages in the PR body.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Ask the user for dev secrets.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in CI or helpers. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops.

<!-- shared-rules:end -->

# TerraScale monorepo

## Development skills

Step-by-step guides live in `.agents/skills/` (also linked from `.claude/skills/`):

- `add-feature`: build a feature end to end (owner, contract, storage, SDKs, UI, docs, journey).
- `add-endpoint`: add an HTTP endpoint with TerraScale.Endpoints.
- `sdk-parity`: add or change an operation across the five SDKs.
- `simple-ui`: design and build dashboard, auth and site screens.

## Layout and commands

- Libraries: `src/TerraScale.Endpoints*` and `src/TerraScale.Logging`. Read `src/TerraBase/AGENTS.md` and `src/TerraScale.Authentication/AGENTS.md` for service rules.
- Projects go in `src/<Project>/`, scripts in `scripts/<area>/`, and docs in `docs/<area>/`. All benchmark code, datasets and results belong in `benchmark/`; .NET benchmarks use `benchmark/dotnet/`. Save comparable Markdown result tables in `benchmark/bench/`.
- Use the root Makefile when a target exists. Build: `make build`; full checks: `make check`. Area targets: `libs-build`, `libs-check`, `auth-build`, `auth-check`, `terrabase-build`, `dashboard-build`, `dashboard-lint`, `benchmark-build`, `benchmark-check`, `docs-build`, `landing-build`, `status-build`.
- SDKs live in `sdk/dotnet`, `sdk/js`, `sdk/go`, `sdk/python` and `sdk/rust`; read each area's `AGENTS.md`. `make sdk-dotnet-check`, `make sdk-js-check`, `make sdk-go-check`, `make sdk-python-check` or `make sdk-rust-check` installs or restores one SDK locked and builds or type-checks it; `make sdk-check` runs all five.
- Auth and TerraBase consume the libraries through ProjectReference. ts-auth consumes sdk/dotnet from the same monorepo revision via `TsDotnetRoot` in `Directory.Build.props`.
- Shared build configuration lives in `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `NuGet.config`, `.editorconfig` and `BannedSymbols.txt`.

## Product rules

- Never fork the Turso database: use upstream tursodatabase/turso at a pinned commit, tarball verified by SHA-256 at build time (benchmark/turso-sync-host/turso-pin.env). Only the Turso client may be forked (today: the vendored driver in src/vendor/Turso.Data.Sqlite), and only when upstream can't do what we need.
- Every feature is always on. No feature flags, opt-in settings, enabled booleans, deploy toggles or kill switches. Delete their guarded paths. Values have one correct setting in code; expose no tuning knobs. No fakes, demo stores or skip-auth: the journey stands in only for third parties (`e2e/AGENTS.md`).
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
- `Microsoft.CodeAnalysis.*` stays at or below the compiler of the SDK pinned in `global.json`, or the analyzers fail to load (CS8032).
- `System.Formats.Cbor` is pinned centrally only because Fido2 4.3.2 asks for 9.0.0 (GHSA-vmv3-wvjp-wqpj); the pin goes when Fido2 asks for a patched version.
- Keep project files minimal. Use ProjectReference for libraries not consumed as packages.
- Warnings are errors in every configuration. Never lower warning levels or weaken analyzer/style enforcement. `NoWarn` is limited to existing justified entries in shared build files. No global suppressions, assembly suppressions, enforced-rule severity reductions or warning pragmas outside generated code. Any necessary local suppression needs a documented justification and human approval.

## Tests

- The journey in `e2e/` is the only test. It drives TerraBase, ts-auth and the dashboard built from the commit under test, and all five SDKs, on every pull request (`journey` job of `ci.yml`, `.github/workflows/journey.yml`). The same specs then run against production after every release and once a day (`.github/workflows/production-journey.yml`, through `e2e/env/production.sh` only). Read `e2e/AGENTS.md` before you change it.
- A behaviour that matters becomes a journey step: a page, flow or SDK operation in `e2e/` (`e2e/AGENTS.md` says how). Never add any other test, test project, fake or fixture.
- `checks` fails a pull request that adds a file outside `e2e/`, or moves one in, whose folder or name marks it as a test. The pattern lives in one place: the `No test file outside the journey` step of `ci.yml`.

## CI

- `.github/workflows/ci.yml` (`CI`) is the only pull-request workflow. `all-green`, its last job, is the one required check: a failed or cancelled job fails it, a skipped job passes. "Re-run failed jobs" re-runs `all-green` in the same run.
- On a pull request, `changes` picks the jobs from the changed paths, each filter listing only the files its job reads, and `checks` runs the policy checks (legacy-name scan, workflow lint, frozen EF Core migrations, storage train, retired-provider grep, no added test file). The legs build and check the merge commit GitHub prepares, as the journey does, and run no test: `libs` and `auth` (locked restore of their solution filter, whitespace format, Release build with analyzers and warnings as errors; libs also checks style, auth its NuGet audit and OpenAPI contract), `terrabase` (the same restore and whitespace check plus the pinned native libraries, then the linux-arm64 single-file release publish as its only build, which fails when a native library lies beside the file), `dashboard` (`dashboard-ci.yml`: the Release build with analyzers and warnings as errors as its only compile, then the lint set, whose format check is whitespace only) and `sdk` (`make sdk-<x>-check` for each changed SDK; `sdk-release.yml` and `scripts/sdk` select none). `docs` compares `docs-site/data/canonical` with the same commit's `landing-site/static/{pricing,regions}.json`, checks its generated pages and builds the site, with no request to production; `landing` and `status` build theirs, and `journey` runs the journey unless every changed file is one it never reads (Markdown, `docs/`, the sites, other workflows, `benchmark/` but the native-library pin `benchmark/turso-sync-host/turso-pin.env`, scripts its builds never run). `test` passes when `changes`, `checks` and every selected leg passed.
- Main push CI runs the journey for every commit. The latest main-push CI for a deploy target must pass `all-green` and both journey shards. A PR run or dispatch cannot replace that proof. `qualified-main` keeps exact-commit qualification for manual TerraBase releases and SDK tags. Build-only and release-only maintenance reuse that proof. Native-artifact maintenance does not qualify a release. TerraBase reaches regions only through a `release-only` dispatch. Release jobs use the newest published ts-orchestrator `ci-` release with `qualification.json` and verify its binary hash.
- Auth and dashboard release jobs stay in `ci.yml` and `images-ghcr.yml`. Their existing OIDC permissions require a main push or dispatch in those files. `Sites` requests a main dispatch only when the shared reconciler finds work due. The dispatched job selects its target again under its release lock. It never deploys the dispatch SHA merely because it triggered the run. Reuse the exact target image or build and check it. Move `:main` only inside that lock after the deploy decision.
- SDK releases run in `.github/workflows/sdk-release.yml` (`SDK Release`). After each green `CI` run of a `main` push it releases the Go CLI when `sdk/go`, `scripts/sdk` or that workflow changed since the newest `sdk/go/v0.0.X` tag (`sdk/go/docs/cli-releases.md`). Older tags without a journey-qualified main-push CI run are refused. Push an SDK tag only after main CI is green for that exact commit. A tag pushed earlier fails and needs a re-run after CI succeeds. A pushed `sdk/<js|python|rust|dotnet>/v0.0.X` tag on `main` qualifies that SDK with `scripts/sdk/build-release.py` and publishes it, each registry from its own job.
- `Production journey` runs after each CI or Images run that released a service. Auth and dashboard write a `deploy-revision` artifact after proving readiness. The journey uses that source, which can differ from the dispatch SHA. Manual TerraBase releases use their exact dispatch commit. Unknown release jobs, missing release steps and missing source receipts fail release detection and report an alert. The daily run and manual dispatch still run the journey. Reports update one open `Production journey failure` issue under the alert lock. A green journey closes it. A run whose live version changes during the journey does not update the alert.
- `CI time` (`.github/workflows/ci-time.yml`) runs the shared `TerraScale/.github` check after each `CI`, `Sites` and `Images (GHCR)` run that succeeded on its first attempt; runs from forks are never measured or used as history. It compares working time (not time waiting for a runner) with the median of the latest five earlier successful first attempts of the same workflow and event (and, outside pull requests, branch) that ran the same jobs, and each job with the slowest of its own latest five successful runs. An issue opens only when the same signal (the same job or the run as a whole) is slower on this run and on the previous run with the same jobs: the `CI time regression: <workflow> (<event>)` issue (label `ci-time`). It never blocks a merge.
- `Dependency advisories` (`.github/workflows/dependency-advisories.yml`) runs weekly and on dispatch, never on a pull request. It lists vulnerable NuGet packages for the solution and for the pinned `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` runtime packs (`linux-arm64` and `linux-x64`), and runs `govulncheck` on `sdk/go`. A finding opens or replaces the `Dependency advisories` issue (label `dependency-advisories`); an empty list closes it. It blocks nothing.
- `Sites` reconciles docs, landing, each status Worker, dashboard and ts-auth after every main CI or Images completion, every 15 minutes, and on a main dispatch. `scripts/ci/site-deploy-gate.mjs` is the shared rule. Under each deploy lock, read live and walk the newest ten main commits. Query the latest main-push CI once per candidate and check the first green run for `all-green` and both journey shards. The first qualified commit is the target. If none qualifies in that bounded walk, on-main live stays live quietly. A scheduled reconcile uses one decision job and only starts deploy jobs for changed things. Equal live or live ahead on main needs no deploy. An ancestor advances only when its build inputs differ. Missing, unreadable or off-main live revisions fail; only a confirmed absent Worker is a first deploy. Every deployed Worker records and proves its target revision at 100% traffic. Failed deployments update one `deployment-alert` issue per thing with target and failed-attempt count. Two failures stop that target until a new target qualifies or a person closes the issue. Successful convergence closes it. Service dispatches wait for active deploy workflow runs; deploy jobs re-select under their release locks.
- Force reconciliation with `gh workflow run sites.yml --ref main`. Dispatch `images-ghcr.yml` for dashboard alone, or `ci.yml -f reconcile_auth=true` for auth alone. Unchanged inputs stop after reading live and target. No run owns a commit handoff. A replaced pending job loses no state. Undo the rule by reverting its merge commit through a PR; see `docs/dev/ci-recovery.md`.
