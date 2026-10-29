# Dashboard development

The dashboard uses .NET 10, RazorSlices, Datastar and the official
ts-dotnet SDK in one production application, `src/TerraScale.Dashboard`.
Node.js and npm build the Lightning CSS stylesheet and browser assets, and
nothing else: the npm project is `src/TerraScale.Dashboard/web` (`package.json`,
`.browserslistrc`, and the scripts in `web/build/`). Tests are C# only.
It is the only implementation; the Go application was removed on 2026-10-12.
Production runs on OCI gru under ts-orch. See [OCI deployment](oci-deployment.md)
for the current contract; [migration status](csharp-migration.md) retains historical qualification evidence.

## Local development

Install the versions pinned in `global.json` and `.mise.toml`, then install
the asset build's dependencies with `npm ci` in `src/TerraScale.Dashboard/web`
(the project build also does this when they are missing). Run local builds, lint, tests and
watchers with low CPU priority:

```sh
nice -n 19 make build
nice -n 19 make lint
nice -n 19 make dev
nice -n 19 make demo
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
Node browser journeys are in `tests/TerraScale.Dashboard.E2E/browser`.

```sh
nice -n 19 make test-e2e
nice -n 19 make test-hot-reload
nice -n 19 make test-live-readiness
nice -n 19 make perf
```

`test-fast` runs the HTTP, MCP, CLI and customer-SDK journeys; `test-slow` runs the
browser journeys and the internal-billing policy suites (`[Category("Slow")]`);
`test-e2e` runs both for local investigation. GitHub Actions is the PR gate; no local test/check run is required before pushing or opening a PR. Use `nice -n 19 make check` (formatting, build, security and Fast) for local investigation. Slow runs independently in GitHub Actions; failures remain red and create or update a GitHub issue.
CI does not run the explicit hot-reload journey. For changes to `scripts/demo.sh`, `scripts/dev.sh`, the asset scripts or the demo host,
use `nice -n 19 make test-hot-reload` on a tailnet machine when investigating hot reload locally.
`check-e2e-shards` lists the discovered tests. The solution contains one
production application and its E2E project; the test executable owns
TUnit discovery, assertions, fixtures and TRX reports. Fixtures start real
Kestrel replicas on operating-system-assigned ports with common test-provider
state and dispose them after execution. They drive Chromium using pinned Playwright dependencies and
an installed headless browser. Reports and screenshots go under ignored
`test-results/`; never commit screenshots.

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

The process/TLS-HTTP E2E journey is `StripeAuditJourneyTests`; its nineteen
focused cases pass. `BillingWebhookJourneyTests` sends signed native HTTP
requests to both webhook URLs, including retryable provider failures and
request-size boundaries. Internal usage reports remain noncharging. These
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

The application consumes the locked ts-dotnet package, whose source revision
and digest are recorded in [SDK provenance](../vendor/dotnet/README.md).
Production state belongs in TerraBase, authorization belongs to ts-auth,
and secrets arrive through ts-orch from OCI Vault using instance principal ([secrets](identity-descriptor.md)). Feature flags,
production demo selection and manual endpoint/secret overrides are prohibited.

Do not deploy until the live SDK, persistence, identity delivery and release
gates in [migration status](csharp-migration.md) are satisfied.
