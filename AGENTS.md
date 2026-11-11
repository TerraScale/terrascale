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
- The shared CI-time check (`ci-time.yml` in `TerraScale/.github`) runs after every CI run. A run more than 30 s slower than the median of the last 5 successful runs of the same workflow opens or updates a `CI time regression: <workflow>` issue. It never blocks a merge; investigate the issue and fix the cause like a bug.
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
- On a pull request, `changes` picks the jobs from the changed paths and `checks` runs the policy checks (legacy-name scan, workflow lint, frozen EF Core migrations, storage train, retired-provider grep, no added test file). The legs build and check what changed and run no test: `libs`, `auth` and `terrabase` (locked restore, pinned native libraries, whitespace format, Release build with analyzers and warnings as errors; libs also checks style, auth its NuGet audit and OpenAPI contract, terrabase that its single-file release publish keeps the native libraries inside), `dashboard` (`dashboard-ci.yml`: the lint set and the Release build) and `sdk` (`make sdk-<x>-check` for each changed SDK). `docs`, `landing` and `status` build the sites, and `journey` runs the journey unless only Markdown, `docs/` or the sites changed. `test` passes when `changes`, `checks` and every selected leg passed.
- On a push to `main` the legs run again on the merged commit: `changes` selects `libs`, `auth`, `terrabase` and `sdk` for the changed areas as on a pull request (a change to TerraBase's server also selects `auth`), and `checks` runs the legacy-name scan. `dashboard`, the sites and `journey` run on pull requests only. When the push changes TerraBase or ts-auth, `main` publishes TerraBase once `test` and `terrabase` passed, and `main-auth` or `main-auth-ordered` publishes and releases ts-auth once `test` and `auth` passed, each from its own Release build (`scripts/ci/image-payloads.sh publish`). A failed leg or build stops its release. TerraBase reaches the regions only through a `release-only` dispatch.
- Releases stay in jobs defined in `ci.yml` (TerraBase, ts-auth) and `images-ghcr.yml` (dashboard): ts-orch accepts release tokens only from those two files, never from a called workflow.
- SDK releases run in `.github/workflows/sdk-release.yml` (`SDK Release`). After each green `CI` run of a `main` push it releases the Go CLI when `sdk/go`, `scripts/sdk` or that workflow changed since the newest `sdk/go/v0.0.X` tag (`sdk/go/docs/cli-releases.md`). A pushed `sdk/<js|python|rust|dotnet>/v0.0.X` tag on `main` qualifies that SDK with `scripts/sdk/build-release.py` and publishes it, each registry from its own job.
- `Production journey` (`.github/workflows/production-journey.yml`) runs the journey against production once after the last release of each commit: when a `CI` or `Images (GHCR)` run of a `main` push or dispatch succeeds, the run starts the journey only if a `main-auth`, `main-auth-ordered`, `release-only` or `Publish and release` job of that commit succeeded, no other `CI` or `Images (GHCR)` run of the commit is still running, and none completed later. It also runs daily at 13:23 UTC and on dispatch, one journey at a time (a newer pending run replaces an older pending one; a running one is never cancelled). It is not a gate: a failure opens the `Production journey failure` issue or replaces the body of the open one, commenting only when the failed tests or the cause changed, and the next green run closes it.
- `CI time` (`.github/workflows/ci-time.yml`) runs the shared `TerraScale/.github` check after each `CI`, `Sites` and `Images (GHCR)` run that succeeded on its first attempt; runs from forks are never measured or used as history. It compares the run with the median of the latest five earlier successful first attempts of the same workflow and event (and, outside pull requests, branch) that ran the same jobs, and each job with the slowest of its own latest five successful runs. When the run is more than 30 s over its median or a job more than 30 s over its slowest, it opens or comments on the `CI time regression: <workflow> (<event>)` issue (label `ci-time`). It never blocks a merge.
- Site deploys run in `.github/workflows/sites.yml` (`Sites`), never in `ci.yml`, so a site deploy cannot turn a `CI` run red. A main push selects each site whose paths its own `before..sha` diff changed. `deploy-docs`, `deploy-landing` and `deploy-status` wait for their site's lock (`docs-deploy`, `landing-deploy`, `status-deploy`), then check out the tip of `main`, build it, deploy it with that commit as the Worker version message, and run `scripts/ci/prove-worker-deploy.mjs`: Cloudflare's records must show each deployed Worker's active deployment serving the version the deploy created at 100%, with that commit as its message. A `Sites` dispatch on `main` redeploys all three; retry a failed deploy with `gh run rerun <run id> --failed` (`docs/dev/ci-recovery.md`).
