# Dashboard development

The active migration uses .NET 10, RazorSlices, Datastar and the official
ts-dotnet SDK in one production application, `TerraScale.Dashboard`.
Node.js and npm build the Lightning CSS stylesheet and browser assets.
The Go application still serves production until migration and producer
qualification are complete. See [migration status](csharp-migration.md) for
outstanding deployment and persistence gates.

The user accepted merging this additive implementation into GitHub `main`
on 2026-10-12 with the known visual and live-service gaps documented. Merge
acceptance does not change the production deployment or qualify a C# cutover.

## Local development

Install the versions pinned in `global.json` and `.mise.toml`, then install
browser/build dependencies with `npm ci`. Run local builds, lint, tests and
watchers with low CPU priority:

```sh
nice -n 19 make dotnet-build
nice -n 19 make dotnet-lint
nice -n 19 make dotnet-dev
nice -n 19 make dotnet-demo
```

The production development host requires automatically delivered platform
identity and persistent services. The demo launcher creates a temporary
test host from sources under `tests/dotnet/demo`; demo stores, identities and
vendor protocol fixtures are excluded from application compilation.
Use the Tailscale URL and worktree-specific port printed by the launcher.
Both launchers bind all interfaces and watch C#/Razor and browser assets.
Same-origin Datastar SSE updates CSS and refreshes pages when source changes.
Stopping the launcher stops its application and asset watchers.

Demo login is `user@example.com` / `test-password`, and email codes are
`123456`. Checkout and billing-portal pages are local test-only vendor pages.
Their transports reject unknown external origins.

## E2E tests

All dashboard tests use **TUnit**, and all tests exercise the running
application through browser, HTTP, MCP or customer SDK boundaries. Do not
create isolated service/component/unit tests or introduce another runner.
Test providers and protocol controls belong under `tests/dotnet/demo`.
Journey tests belong under `tests/dotnet/e2e`, in `*JourneyTests.cs` files.

```sh
nice -n 19 make dotnet-test-e2e
nice -n 19 make dotnet-parity-reference
nice -n 19 make dotnet-parity
nice -n 19 make dotnet-test-hot-reload
nice -n 19 make dotnet-test-live-readiness
```

`test-fast`, `test-slow`, `test-browser`, `dotnet-test-fast` and
`dotnet-test-browser` all invoke the TUnit E2E executable. The solution
contains one production application; the separate test executable owns
TUnit discovery, assertions, fixtures and TRX reports. Fixtures start real
Kestrel replicas on operating-system-assigned ports with common test-provider
state and dispose them after execution. They drive Chromium using pinned Playwright dependencies and
an installed headless browser. Reports and screenshots go under ignored
`test-results/`; never commit screenshots.

For a focused journey, use the TUnit tree-node filter:

```sh
nice -n 19 dotnet run --configuration Release \
  --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- \
  --treenode-filter '/*/*/BillingBrowserJourneyTests/*' --report-trx \
  --results-directory "$PWD/test-results/tunit"
```

Use an absolute results directory because the test executable can resolve a
relative path under its build output. Billing journeys capture current UTC
time once and carry the independently calculated month into their assertions;
fixed historical clocks can make real HTTP clients discard expired cookies.

The strict visual journey compares the original Go application's captured
desktop/mobile states with C# using status, DOM, controls, geometry and every
pixel. `dotnet-parity-reference` captures that original implementation;
TUnit starts a plain Go reference executable and owns capture, comparison
and failure reports. Reference capture is explicit and excluded from the
normal E2E run, so a comparison never replaces its baseline. Capture also
exports the actual source account and workspace IDs through authenticated
HTTP/MCP, after screenshots. Missing reference artifacts
fail the journey. Fixture and feature differences remain failures.

The hot-reload journey starts the test-only watcher, opens its Tailscale
origin, changes C# copy and CSS, verifies both in the browser, restores the
files and verifies the original rendering. Run it with the worktree's demo
port free and without concurrent edits to the auth copy or CSS fixtures.

Lint verifies SDK package provenance, the E2E-only test boundary and formatting
of the application and test project. The live account-to-ts-typescript-SDK
journey and persistent restart qualification remain required; a passing
demo cannot substitute for either.

The explicit `dotnet-test-live-readiness` journey launches the genuine
production executable with no demo/provider overrides, then requires startup
and HTTP readiness. Normal startup establishes the fixed state schema using
the automatically delivered identity. Missing delivery or unavailable
services fail this test. It covers state readiness; the live account-to-SDK
flow, authentication and Stripe delivery need their own qualification.

`audit-stripe` runs the read-only Stripe.net report using the fixed machine
identity descriptor at `/run/secrets/terrascale/dashboard.json`; it accepts no
configuration options. Run it with:

```sh
nice -n 19 dotnet run --project TerraScale.Dashboard.csproj -- audit-stripe
```

The process/TLS-HTTP E2E journey is `StripeAuditJourneyTests`; its nineteen
focused cases pass. `BillingWebhookJourneyTests` sends signed native HTTP
requests to both webhook URLs, including retryable provider failures and
request-size boundaries. Internal usage reports remain noncharging. These
test-provider results do not qualify live Stripe delivery or payments; there
is no separate unit-test runner.

## Assets and production integration

Lightning CSS bundles `web/assets` into the single stylesheet
`web/static/css/terrascale.css`. esbuild bundles the self-hosted Datastar and
Monaco scripts under `web/static/js`. Datastar version, source map and license
are pinned together; the build validates their recorded hashes. Monaco
loads only on editor pages, and map code/data loads only where needed.
Brand marks are file-backed SVG assets under `web/static/brand`.

The application consumes the locked ts-dotnet package, whose source revision
and digest are recorded in [SDK provenance](../vendor/dotnet/README.md).
Production state belongs in TerraBase, authorization belongs to ts-auth,
and secrets must arrive automatically from OCI Vault. Feature flags,
production demo selection and manual endpoint/secret overrides are prohibited.

The existing Go sources, deployment and browser fixtures are retained as
migration references. Do not deploy C# or remove that reference until the
feature, visual, live SDK, persistence and release gates are satisfied.
