# Go dashboard development

The repository pins Go 1.27.1 in `.mise.toml`. Use Node.js 24 with npm for the
Lightning CSS bundle, the esbuild script bundle, and the Playwright driver used
by the Go browser tests. At the repository root:

```sh
mise install
npm ci
```

## Local server

`make dev PORT=5481` binds the dashboard to `0.0.0.0:5481`, enables the
in-memory demo backend, builds the browser scripts once, and starts three
development watchers. Air watches Go
and `.templ` source files. For each application build it runs the pinned
templ generator against `internal/` with version and timestamp metadata
disabled, then compiles the server. Generated `*_templ.go` files stay beside
their `.templ` sources and are not edited by hand; Air excludes those generated
files from its watch loop. Run `make generate` after editing components, or
`make check-generated` to verify that generated output is current.
With `stop_on_error = false`, Air keeps the last successfully built executable
available if a temporary source error prevents a new build. It reports the
compile error, restarts that previous binary, and tries again when watched
files change.

The independent `npm run watch:css` process bundles CSS sources with
Lightning CSS into the single stylesheet at
`web/static/css/terrascale.css`, and `npm run watch:js` rebuilds the browser
scripts in `web/static/js/` when `web/assets/js/` changes. With `DASHBOARD_DEV=1` every page opens a
Datastar stream on `/__dev/events`: the Go host patches the stylesheet link
when the bundle changes and sends a boot ID, so pages reload after Air
restarts the server. When Air is not installed,
the script installs pinned Air v1.67.4 with `go install`; that development-tool
bootstrap needs access to the Go module proxy. The dashboard and templ
generator build from vendored modules. Stopping the foreground `make dev`
process with Ctrl+C terminates Air, its app child, and both asset watchers.

Page behavior uses the official Datastar v1.0.4 bundle, vendored with its
source map and license under `web/assets/js/vendor/`. `scripts/build-js.mjs`
checks the bundle and source map against the hashes in `internal/ui/datastar_pins.go`,
rejects remote dynamic imports, and copies these files into `web/static/js/`.
Pages load it from the dashboard using a content-hashed URL, without a CDN
request. When upgrading, replace the official bundle, source map and license
together and update both hashes in `internal/ui/datastar_pins.go` from the
official release. Git
preserves the bundle's bytes without normalizing line endings.
Behavior lives in Go handlers, templ components, native HTML (`<details>`,
`<dialog>` with `command`/`commandfor`) and Datastar attributes. The one custom
script is the code editor (`web/assets/js/code-editor/`): `ui.CodeEditor`
renders a textarea that it upgrades into Monaco (modern-monaco) once the page
has loaded, and only pages that render one load the script and widen the
Content-Security-Policy for its styles. `scripts/build-js.mjs` bundles it with
esbuild into the generated, uncommitted `web/static/js/`, self-hosting every
Monaco chunk, worker, grammar, and font; release builds (`--release`, used by
the Dockerfile) add brotli siblings the asset handler serves in their place. Starbase informs the dashboard's visual direction; it is
a design reference, and no Starbase framework code is copied into the app.

`DASHBOARD_DEMO_ADMIN_EMAIL` and `DASHBOARD_DEMO_ADMIN_PASSWORD` seed the local
test administrator. The development script supplies test-only defaults; set
both variables to replace them. Do not use those defaults for shared or
production environments.

`TS_DASHBOARD_CSRF_SECRET` (at least 32 bytes) keys the CSRF tokens. Every
machine serving one deployment must share it, so it is required outside the
demo; on Fly.io set it as an app secret. When the demo runs without it, each
process draws a random key.

Use `make serve PORT=5481` for a plain HTTP server without Air or asset
watching.
Set `DASHBOARD_E2E_BASE_URL` when the browser tests should exercise an
already-running server instead of starting the dashboard in-process.

## Build and tests

```sh
make lint       # gofmt check and go vet
make check-modules # verify public dependency downloads and checksums
make check-e2e-shards # verify every browser test runs in exactly one CI shard
make generate   # regenerate Go components from internal/**/*.templ
make check-generated # verify generated templ Go sources are current
make css        # bundle web/assets into web/static/css/terrascale.css
make js         # bundle web/assets/js into the generated web/static/js/
make test-fast  # Go browser journeys with -race (skips the SDK journey)
make test-slow  # full Go browser suite including the account-to-ts-go journey
make test-unit  # test-slow's package unit tests only
make test-browser E2E_SHARD=2/4  # one round-robin share of test-slow's browser tests
make perf       # Core Web Vitals report in test-results/performance.json
make build      # CSS and script bundles and optimized bin/dashboard-release
```

The component source is `.templ`; Go source files ending in `_templ.go` are
generated by templ v0.3.1020, pinned as a Go tool dependency. The Makefile uses
the local Go toolchain and vendored modules for generation. Both `make lint`
and `make build` verify generated-source freshness before proceeding. The Go
build embeds `web/static/js/`, so `lint`, `build`, `serve`, and the test targets
bundle the scripts first; run `make js` before plain `go build` or `go test`.

`make test-fast` and `make test-slow` run the package unit tests under
`internal/` and the Go integration tests in `tests/e2e/`. The integration tests
drive Chromium with [playwright-go](https://github.com/mxschmitt/playwright-go)
using the `playwright-core` driver pinned in `package.json`; no driver is
downloaded at test time. When the driver's own Chromium revision is not installed, the suite
falls back to the newest headless shell under `PLAYWRIGHT_BROWSERS_PATH`
(the CI image ships one). Each test uses its own browser context and account,
but Chromium journeys run serially to avoid timeouts under the race detector.
Use `-parallel 1` when running `go test ./tests/e2e` directly. The accessibility
tests inject axe-core, pinned in `package.json`, into the signed-out,
onboarding, member and admin pages and
their dialogs and menus, and fail on any WCAG 2.2 level A or AA violation.
Set `DASHBOARD_E2E_SCREENSHOTS=<dir>` to capture desktop and mobile
screenshots.

`tests/e2e/page_weight_test.go` loads the main pages with a cold cache and
fails when the compressed document, stylesheet, fonts, scripts, or images of a
page exceed their budget; raise a budget only when the extra bytes are
intended. `make perf` measures Core Web Vitals on desktop and throttled mobile
profiles, each page once with a cold cache and once warm; set `PERF_BASELINE`
to a previous report to fail on regressions.

The SDK journey registers through the browser, creates databases and scoped
API keys against an in-process public API fixture, then uses the vendored
`ts-go` SDK to write, read, and delete a document. The fixture exists only
inside the test binary.

The app and Docker build use `GOFLAGS=-mod=vendor`. `vendor/PROVENANCE.md`
records the source commit for the vendored `ts-go` package. Do not replace this
with a sibling checkout path in `go.mod`; standalone CI and Docker builds must
work from the repository contents.

The SDK declares the `terrascale.tech/sdk` module path, but that vanity domain
does not yet serve Go module metadata. A versioned `replace` in `go.mod`
resolves it through the [public ts-go repository](https://git.terrascale.tech/TerraScale/ts-go).
The pin keeps the dashboard's current API contract; it does not track the
SDK's main branch. Downloads use the public Go proxy with direct HTTPS fallback
and the public checksum database, without credentials or a local module proxy.
The repository's `go-import` metadata points to
`https://git.terrascale.tech/TerraScale/ts-go.git`. The public replacement,
including its pinned revision and checksum records, is intentionally part of
the CI download check; it does not require a Codeberg mirror.

To upgrade the SDK, update both its `require` version and the public repository
version in `replace`, then run `GOFLAGS=-mod=mod go mod tidy` and
`GOFLAGS=-mod=mod go mod vendor`. Third-party module updates use those same
commands. `go mod vendor` deletes `vendor/PROVENANCE.md`; restore it and update
its source commit and checksums for SDK upgrades.

The vendored SDK includes the regional API hostname correction documented in
`vendor/PROVENANCE.md`. Reapply it after vendoring the current pin; for upgrades,
check whether the chosen upstream revision already contains the correction.
`make check-modules` verifies
public dependency resolution in its own CI job, alongside lint, unit tests,
and browser shards. The workflow's consecutive shard matrix is the single
source of truth for the total, read by `scripts/e2e-shard-count.sh` for both
CI runs and local coverage checks. The lint job verifies that the actual shard patterns
cover every listed browser test exactly once, and that invalid shard inputs
are rejected. Shard helpers respect `GO`, and browser jobs log their resolved
test pattern. Public module verification needs no npm install or browser assets.
It uses the installed Go toolchain and a temporary module cache, removed on
exit, so a warm runner cache cannot hide download failures. The check downloads
the full module graph, including tool and transitive module dependencies.
It disables the saved Go environment file with `GOENV=off` and clears Go's [module privacy settings](https://go.dev/ref/mod#private-modules),
`GOPRIVATE`, `GONOPROXY`, `GONOSUMDB`, and `GOINSECURE`, so inherited exclusions cannot
bypass the public proxy or checksum database. Cleanup makes the throwaway
cache writable and removes the directory directly, even if Go fails to start,
while preserving the download exit status.

The MCP endpoint and its tool conventions are described in [mcp.md](mcp.md).

## Fly runtime

`fly.toml` configures the production app for one warm machine. Its process-local
accounts and sessions must remain on one machine and do not survive restarts.
`fly.preview.toml` configures one machine per preview and allows it to stop
while idle, so preview accounts are reset when it wakes. Both configurations
probe `GET /health/ready` and set `TS_DASHBOARD_PRODUCTION_DEMO=1`.

### State backend

`TERRABASE_URL`, `TERRABASE_API_KEY`, `TERRABASE_PROJECT_ID` and
`TERRABASE_DATABASE` (all four, or none) select the TerraBase Documents store;
it then also needs a stable `TS_DASHBOARD_CSRF_SECRET` of at least 32 bytes.
Unset, the dashboard keeps the in-memory store above. The operator command
`dashboard import-org` additionally reads `TERRABASE_MANAGEMENT_URL`,
`TERRABASE_MANAGEMENT_CREDENTIAL` and `TERRABASE_MANAGEMENT_USER_ID`. See
[terrabase-cutover.md](terrabase-cutover.md).

The health probes answer `{"status":"ok","version":"<commit>","backend":"memstore"}`
(`terrabase` when configured; readiness then reads TerraBase and answers 503
when it is unreachable); the status
page records a deploy marker whenever the version changes. The deploy
workflows pass the commit as the `VERSION` build argument, which the
Dockerfile links into `internal/platform/buildinfo`; builds without it report
Go's embedded VCS revision, or `dev`.

The signed-in shell shows a banner for unresolved incidents and for
maintenance in progress or starting within 24 hours, read from the status
page's Statuspage-compatible summary
(`https://status.terrascale.tech/api/v2/summary.json`). An incident quotes
the first paragraph of its latest update, cut to about 200 characters and
two lines; "View status" opens the full update. Set
`DASHBOARD_STATUS_SUMMARY_URL` to read another summary, or to an empty value
to disable the banner. The summary is fetched server-side with a two-second
timeout and cached for 30 seconds; when it cannot be read, no banner is shown.

### Error tracking

Errors go to GlitchTip through the official Sentry Go SDK
(`internal/platform/errortracking`). With `SENTRY_DSN` set, the dashboard
reports handler panics (then lets `net/http` abort the response as before),
every 5xx response grouped by its route, and every `slog` record at error
level, with an `error` attribute as the exception. Events carry the build
version as the release and `SENTRY_ENVIRONMENT` as the environment, which
defaults to `production` on Fly.io and `development` elsewhere. Request
bodies, query strings, cookies and the signed-in user are never sent (the
CLI approval URL carries its `user_code` in the query, also nested in
`returnTo`), and request headers keep their values only for `Accept`,
`Content-Type`, `Content-Length`, `Datastar-Request` and `User-Agent`;
`Referer` would repeat such a URL. Without `SENTRY_DSN`, nothing is
reported.

`DASHBOARD_CSP_REPORT_URL` adds a `report-uri` directive to every page's
Content-Security-Policy so browsers post violations to GlitchTip's security
endpoint. There is no `report-to` directive: GlitchTip only accepts the
legacy `application/csp-report` body, not the Reporting API's.

`fly.toml` and `fly.preview.toml` set both; previews report as the `preview`
environment.

The e2e workflow runs lint and the full Go browser and SDK suite on every pull
request and push to `main`. The production and preview workflows build the
image on Fly's remote builder (`fly deploy --remote-only`), so they need no Go
or Node.js toolchain on the runner; the Dockerfile keeps the Go build and npm
caches in BuildKit cache mounts. Production deploys one warm machine and the
preview workflow deploys one machine per pull request. No live deployment is
required for local development or tests.

## Public device authorization origin

Set `DASHBOARD_PUBLIC_URL` to the dashboard's public HTTP(S) origin, such as
`https://dashboard.terrascale.tech`, when serving a custom domain. Paths,
credentials, queries, and fragments are rejected at startup. CLI verification
links use this configured origin regardless of the request's Host header.

On Fly, an unset value defaults to `https://$FLY_APP_NAME.fly.dev`, which also
keeps PR previews tied to their own app. Without either setting, device-code
issuance accepts only a loopback Host for local development. Pending grants are
limited to ten per client address (using the same trusted proxy header and IPv6
/64 grouping as password throttling) and 1,000 per demo process. An approved
grant remains available for retry when the account's access-token quota is full;
revoke an existing token and poll again after the advertised interval.
