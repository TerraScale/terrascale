# Dashboard development

The dashboard uses .NET 11, RazorSlices, Datastar, Entity Framework Core over the
TerraBase provider for its state and MagicOnion over the shared contracts library
for TerraBase's product APIs, in one production application, `src/TerraScale.Dashboard`.
Node.js and npm build the Lightning CSS stylesheet and browser assets, and
nothing else: the npm project is `src/TerraScale.Dashboard/web` (`package.json`,
`.browserslistrc`, and the scripts in `web/build/`).
It is the only implementation; the Go application was removed on 2026-10-12.
Production runs on OCI gru under ts-orch. See [OCI deployment](oci-deployment.md)
for the current contract; [state storage](state-storage.md) retains historical qualification evidence.

## Local development

Install the .NET SDK pinned in root `global.json` and the asset tooling pinned in `src/TerraScale.Dashboard/web/package-lock.json`, then install
the asset build's dependencies with `npm ci` in `src/TerraScale.Dashboard/web`
(the project build also does this when they are missing). Run local builds, lint and
watchers with low CPU priority:

```sh
nice -n 19 make dashboard-build
nice -n 19 make dashboard-lint
nice -n 19 make -f scripts/dashboard/Makefile dev
```

The dotted region map, `web/static/brand/region-map.svg`, is committed. Only
when its artwork changes, regenerate it with `npm run build:region-map` in
`src/TerraScale.Dashboard/web`; the project build never runs it.

The development host requires automatically delivered platform identity and
persistent services. Use the Tailscale URL and worktree-specific port printed by
the launcher. It binds all interfaces and watches C#/Razor and browser assets.
Same-origin Datastar SSE updates CSS and refreshes pages when source changes.
Stopping the launcher stops its application and asset watchers.

## Tests

The journey in `e2e/` is the only test. It audits every dashboard page and drives
its flows against the real TerraBase and ts-auth built from the same commit
([`e2e/AGENTS.md`](../../e2e/AGENTS.md)); a new page or tab is one state in
`e2e/pages/inventory.ts`.

Images (GHCR) is the dashboard's only main workflow: every push that changes a dashboard input and every dispatch runs
the dashboard checks (`dashboard-ci.yml`: the lint set, the Release build and the publish of
that build as the `dashboard-publish` artifact), and the push run waits for that commit's CI, publishes the image and releases it to every region automatically, with
no manual step and no deploy switch. The release token's sha must equal the image's
revision, so each qualified commit is released by its own push run, even after later
pushes outside the dashboard move main: `scripts/dashboard/ci/release-gate.py` admits
the newest qualified main commit whose CI passed and that is newer than live. The
regions' release verifier accepts a run's GitHub OIDC token only from push and dispatch
events, so the workflow_run-triggered run never deploys; after each CI completion it
reruns an existing push/dispatch Images run if it ended without releasing it, or dispatches
Images when the qualified revision is still main head and its push event was missed.
Normal main CI dispatches qualify through the same required quality gate; native
maintenance cannot qualify. See [CI recovery](../dev/ci-recovery.md).

Lint checks that the committed stylesheet matches `web/assets` and
`Features/**/*.css`, then verifies SDK package provenance and the formatting of the
application.

`audit-stripe` runs the read-only Stripe.net report using the Stripe keys from
the dashboard's OCI Vault secret files ([secrets](identity-descriptor.md)); it accepts no
configuration options. Run it with:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

## Assets and production integration

Paths below are relative to `src/TerraScale.Dashboard`. Lightning CSS
bundles `web/assets` and `Features/**/*.css` into the single stylesheet
`web/static/css/terrascale.css`. esbuild bundles the self-hosted Datastar and
Monaco scripts under `web/static/js`. The project build runs both bundlers.
Datastar version, source map and license are pinned together; the build
validates their recorded hashes. Monaco
loads only on editor pages, and map code/data loads only where needed.
Brand marks are file-backed SVG assets under `web/static/brand`.

The application keeps its state in relational tables through the TerraBase EF
Core provider ([state storage](state-storage.md)) and reaches TerraBase's product
APIs over MagicOnion through the shared contracts library.
Production state belongs in TerraBase, authorization belongs to ts-auth,
and secrets arrive through ts-orch from OCI Vault using instance principal ([secrets](identity-descriptor.md)). Feature flags,
production demo selection and manual endpoint/secret overrides are prohibited.

Do not deploy until the live SDK, persistence, identity delivery and release
gates in [state storage](state-storage.md) are satisfied.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
