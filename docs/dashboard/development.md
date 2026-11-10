# Dashboard development

The dashboard uses .NET 11, RazorSlices, Datastar, Entity Framework Core over the
TerraBase provider for its state and MagicOnion over the shared contracts library
for TerraBase's product APIs, in one production application, `src/TerraScale.Dashboard`.
Node.js and npm build the Lightning CSS stylesheet and browser assets, and
nothing else: the npm project is `src/TerraScale.Dashboard/web` (`package.json`,
`.browserslistrc`, and the scripts in `web/build/`). Tests are C# only.
It is the only implementation; the Go application was removed on 2026-10-12.
Production runs on OCI gru under ts-orch. See [OCI deployment](oci-deployment.md)
for the current contract; [state storage](state-storage.md) retains historical qualification evidence.

## Local development

Install the .NET SDK pinned in root `global.json` and the asset tooling pinned in `src/TerraScale.Dashboard/web/package-lock.json`, then install
the asset build's dependencies with `npm ci` in `src/TerraScale.Dashboard/web`
(the project build also does this when they are missing). Run local builds, lint, tests and
watchers with low CPU priority:

```sh
nice -n 19 make dashboard-build
nice -n 19 make dashboard-lint
nice -n 19 make -f scripts/dashboard/Makefile dev
nice -n 19 make -f scripts/dashboard/Makefile demo
```

The dotted region map, `web/static/brand/region-map.svg`, is committed. Only
when its artwork changes, regenerate it with `npm run build:region-map` in
`src/TerraScale.Dashboard/web`; the project build never runs it.

The production development host requires automatically delivered platform
identity and persistent services. The demo launcher creates a temporary
host around the test-only synthetic providers
(`tests/TerraScale.Dashboard.E2E/Demo`).
Use the Tailscale URL and worktree-specific port printed by the launcher.
Both launchers bind all interfaces and watch C#/Razor and browser assets.
Same-origin Datastar SSE updates CSS and refreshes pages when source changes.
Stopping the launcher stops its application and asset watchers.

Sign-in redirects to a test-only stand-in for ts-auth's hosted pages that signs
you in as `user@example.com`. Checkout and billing-portal pages are local test-only vendor pages.
Their transports reject unknown external origins.

## E2E tests

All dashboard tests use **TUnit**, and all tests exercise the running
application through browser, HTTP, MCP or customer SDK boundaries. Do not
create isolated service/component/unit tests or introduce another runner.
Scenario and protocol controls belong under
`tests/TerraScale.Dashboard.E2E/Demo`, alongside shared synthetic providers. Journey tests belong under
`tests/TerraScale.Dashboard.E2E`, in `*JourneyTests.cs` files.
Test only the happy path and critical paths (sign-in, tenant isolation, billing,
erasure, backup/restore, transport and secret safety). Do not add copy, layout
or per-page accessibility tests.
Node browser journeys are in `tests/TerraScale.Dashboard.E2E/browser`.

```sh
nice -n 19 make -f scripts/dashboard/Makefile test-e2e
nice -n 19 make -f scripts/dashboard/Makefile test-hot-reload
nice -n 19 make -f scripts/dashboard/Makefile test-live-readiness
nice -n 19 make -f scripts/dashboard/Makefile perf
```

`test-fast` runs the HTTP, MCP, CLI and customer-SDK journeys;
`test-slow` runs the browser journeys and the internal-billing policy suites
(`[Category("Slow")]`), or one journey with `TESTS=Class.Method`;
`test-e2e` runs both for local investigation. Ship with plain git from the monorepo root
([Shipping](../dev/shipping.md)); dashboard Fast must finish in under 30 s wall time; each single test must finish in under 10 s.
A Fast suite over 30 s or a test over 10 s is a bug. Keep only happy-path and
critical-path tests in Fast, move heavy critical tests to Slow and delete everything
else. Delete at least 80% of tests against the 2026-11-04 workspace policy baseline.
New features add at most one happy-path and one critical-failure test.
Images (GHCR) is the dashboard's only main workflow: every push and dispatch runs
Dashboard Fast (`dashboard-ci.yml`: `make dashboard-test-fast`, then it publishes the tested
application as the `dashboard-publish` artifact), and the push run waits for that commit's CI, publishes the image and releases it to every region automatically, with
no manual step and no deploy switch. The release token's sha must equal the image's
revision, so each qualified commit is released by its own push run, even after later
pushes outside the dashboard move main: `scripts/dashboard/ci/release-gate.py` admits
the newest qualified main commit whose CI passed and that is newer than live. The
regions' release verifier accepts a run's GitHub OIDC token only from push and dispatch
events, so the workflow_run-triggered run never deploys; after each CI completion it
reruns an existing push/dispatch Images run if it ended without releasing it, or dispatches
Images when the qualified revision is still main head and its push event was missed.
Normal main CI dispatches qualify through the same required quality gate; native
maintenance cannot qualify. See [CI recovery](../dev/ci-recovery.md). Dashboard Slow runs in
`slow.yml` with the auth and TerraBase Slow suites, on `main` every 3 hours when its inputs
changed; each failure stays visible and creates or updates a matching GitHub issue.
CI does not run the explicit hot-reload journey. For changes to `scripts/dashboard/demo.sh`, `scripts/dashboard/dev.sh`, the asset scripts or the demo host,
use `nice -n 19 make -f scripts/dashboard/Makefile test-hot-reload` on a tailnet machine when investigating hot reload locally.
`check-e2e-shards` lists the discovered tests. The solution contains one
production application and its E2E project; the test executable owns
TUnit discovery, assertions, fixtures and TRX reports. Fixtures start real
Kestrel replicas on operating-system-assigned ports with common test-provider
state and dispose them after execution. They drive Chromium using pinned Playwright dependencies and
an installed headless browser. Reports and screenshots go under ignored
`test-results/`; never commit screenshots.

### Writing a Fast journey

Each `DemoEnvironment` creates its own TerraBase database and replays every migration into it, which
costs TerraBase about half a CPU-second. With one environment per test that was most of the suite's CPU
on the 4 vCPU runner, so Fast journeys share hosts and isolate themselves by account instead:

- Take a session-wide fixture with `[ClassDataSource<T>(Shared = SharedType.PerTestSession)]`:
  `DashboardE2EFixture` (a dashboard host pair), `SharedDatabaseSdk` (the customer-SDK producer
  and its host pair), `SharedAuthProvider` (the hosted sign-in stand-in for ts-auth and its host
  pair), `SharedDemoEnvironment` (the fakes and database, for a journey that starts its own
  configured host) or `SharedTsAuthPlatform` (the ts-auth platform producer and its host pair).
  They live in `tests/TerraScale.Dashboard.E2E/Demo/SharedScenarios.cs`, and
  `scripts/dashboard/verify-e2e.py` (run by `make dashboard-lint`) rejects `PerTestSession` on any
  other type: add a new shared fixture to that file and to this list instead of sharing state from a
  test class.
- Sign up a fresh account in each journey (`DatabaseJourney.NativeWorkspaceAsync`, or
  `HttpJourney.NewAccount` with `LoginAsync(..., signUp: true)`) and act only on its own
  organizations, projects and databases. Never sign in as the seeded `user@example.com`
  (`test-user`) on a shared fixture: concurrent sign-ins of one account race.
- Producer behaviour for one journey goes through `DatabaseSdkScenario.Intercept(projectId, ...)`,
  which sees only requests for that project. The Stripe, billing-plan and reporting fakes are already
  keyed by customer, organization or user.
- A journey that changes state every other journey would see (a producer `/__test/control` command
  such as `role`, `catalog`, `fault`, `advance` or `platform-role`, a global counter or observation
  list, account deletion, the clock) starts its own scenario with `StartAsync`. Keep those few.

To check a change against the budget, run the built suite through the shared gate:
`python3 scripts/ci/fast-budget.py dashboard -- dotnet run --project tests/TerraScale.Dashboard.E2E
--configuration Release --no-build -- --treenode-filter '/*/*/*/*[Category!=Slow]'`. It fails when
the suite takes 30 s or more or any test more than 10 s, prints the ten slowest tests and names
every test over 10 s; an over-budget suite still runs to the end (it is stopped only after 120 s).

For a focused journey, use the TUnit tree-node filter:

```sh
nice -n 19 dotnet run --configuration Release \
  --project tests/TerraScale.Dashboard.E2E -- \
  --treenode-filter '/*/*/BillingBrowserJourneyTests/*' --report-trx \
  --results-directory "$PWD/test-results/tunit"
```

Use an absolute results directory because the test executable can resolve a
relative path under its build output. Billing journeys capture current UTC
time once and carry the independently calculated month into their assertions;
fixed historical clocks can make real HTTP clients discard expired cookies.

The Go application, its reference capture and the strict visual parity
journey were removed; the seed fixture captured from it lives at
`tests/TerraScale.Dashboard.E2E/Fixtures/reference-workspace.json`.

The hot-reload journey starts the test-only watcher, opens its Tailscale
origin, changes C# copy and CSS, verifies both in the browser, restores the
files and verifies the original rendering. Run it with the worktree's demo
port free and without concurrent edits to the auth copy or CSS fixtures.

Lint checks that the committed stylesheet matches `web/assets` and
`Features/**/*.css`, then verifies SDK package provenance, the E2E-only test boundary and formatting of the
application and test project. The live account-to-ts-typescript-SDK
journey and persistent restart qualification remain required; a passing
demo cannot substitute for either.

The explicit `test-live-readiness` journey launches the genuine
production executable with no demo/provider overrides, then requires startup
and HTTP readiness. Normal startup establishes the fixed state schema using
the automatically delivered identity. Missing delivery or unavailable
services fail this test. It covers state readiness; the live account-to-SDK
flow, authentication and Stripe delivery need their own qualification.

`audit-stripe` runs the read-only Stripe.net report using the Stripe keys from
the dashboard's OCI Vault secret files ([secrets](identity-descriptor.md)); it accepts no
configuration options. Run it with:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

`BillingWebhookJourneyTests` sends signed native HTTP requests to both webhook
URLs. Internal usage reports remain noncharging. These
test-provider results do not qualify live Stripe delivery or payments; there
is no separate unit-test runner.

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
