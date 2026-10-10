# Go dashboard development

The repository pins Go 1.27.1 in `.mise.toml`. Use Node.js 24 with npm for the
Lightning CSS bundle, the esbuild script bundle, and the Playwright driver used
by the Go browser tests. At the repository root:

```sh
mise install
npm ci
```

## Local server

The current `make dev` launcher binds the dashboard to all interfaces, selects
an in-memory demo backend, builds browser scripts and starts three watchers.
This is legacy launcher inventory: production and shared previews must use
real persistence/authentication, and fake backends must move to test binaries. Air watches Go
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
`<dialog>` with `command`/`commandfor`) and Datastar attributes. The shared
`web/assets/js/button-loading.js` script replaces action button icons with a
spinner during native submissions, button-link navigations, and Datastar requests, blocks repeated
submissions, and restores the controls after completion or navigation
cancellation. The code editor (`web/assets/js/code-editor/`) upgrades the
textarea rendered by `ui.CodeEditor` into Monaco (modern-monaco) once the page
has loaded. Only pages that render an editor load its script and widen the
Content-Security-Policy for its styles. `scripts/build-js.mjs` bundles it with
esbuild into the generated, uncommitted `web/static/js/`, self-hosting every
Monaco chunk, worker, grammar, and font; release builds (`--release`, used by
the Dockerfile) add brotli siblings the asset handler serves in their place. Starbase informs the dashboard's visual direction; it is
a design reference, and no Starbase framework code is copied into the app.

Production CSRF and sign-in secrets must arrive automatically from OCI Vault;
demos, seeded administrators and fake backends belong only in test binaries.
The current development launcher still selects a demo runtime and accepts
`DASHBOARD_*` overrides. That path is an implementation gap, not a production
or shared-preview setup recommendation. Use the isolated test suites for fake
accounts and stores.

`DASHBOARD_E2E_BASE_URL` selects an already-running test target; it does not
activate a product feature.

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
public dependency resolution in the `check` CI job, alongside lint; unit tests
and browser shards. The workflow's consecutive shard matrix is the single
source of truth for the total, read by `scripts/e2e-shard-count.sh` for both
CI runs and local coverage checks. The check job verifies that the actual shard patterns
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

Production must use persistent TerraBase state, native ts-auth sign-in and billing.
All service endpoints and region identity must be discovered automatically;
OCI Vault provisions and delivers service, CSRF and vendor Stripe credentials.

**Current gaps:** runtime backend selection still depends on `TERRABASE_*`
inputs and can fall back to memory. Preview configuration still selects a demo.
These paths must be removed from production binaries/configuration; this docs
change does not change them. Readiness must fail closed on unavailable storage.

Platform admin sign-in mirrors authorized inventory through ts-go Management.
The temporary platform-admin grant remains a restricted authorization action,
not automatic privilege elevation. See [terrabase-cutover.md](terrabase-cutover.md).

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
(`https://status.terrascale.tech/api/v1/summary.json`). An incident quotes
the first paragraph of its latest update, cut to about 200 characters and
two lines; "View status" opens the full update. The status source has one correct setting;
the current URL/empty-value override is a runtime gap, not a banner kill switch. The summary is fetched server-side with a two-second
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

The ci workflow runs lint and the full Go browser and SDK suite on every pull
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

## Sign-in design (do not revert)

A user decision (TS-582): users sign in on **ts-dashboard's own pages**, never
on ts-auth's hosted page.

- `/login` (password), `/login/code` (emailed code), `/onboarding` (sign-up)
  and `/reset-password` are dashboard pages. The backend calls ts-auth's
  headless API: the password and email-code grants, `email-code/start`,
  `sign-up` and `password-reset/*`. Shipped in #180, fixed in #186.
- Only single sign-on and social providers (`/login/sso`) redirect to ts-auth's
  hosted authorize page.
- With `TS_AUTH_ISSUER` set, `GET /login` renders the dashboard's form
  (`type="password"`, links to `/login/code` and `/login/sso`). It never
  answers 303 to `<region>.auth.terrascale.tech/.../oauth/authorize`, even
  when discovery omits headless grants or ts-auth is unavailable. API failures
  stay on the native pages; `/login/sso` remains the explicit redirect.
- **Incidents: fix forward.** Never roll the dashboard back past native login
  to "restore" sign-in, because that brings back the hosted page. The only
  allowed rollback target is the newest image that still has working native
  login (#186, `9c7d62c3`, or later).
- Tests that cover sign-in must use the TerraBase-backed stores (throttle,
  sessions) as production does, not only the in-memory ones (the #180 → #186
  incident). `TestNativeSignInThrottlesWithTerraBase` in `internal/app` does.
- After each production deploy, `scripts/smoke-native-login.sh` checks that
  `https://dash.terrascale.tech/login` renders the form, and that a password
  sign-in for an unknown email is refused by ts-auth (401), not throttled
  (429). On failure the deploy job fails, and it rolls back only to a
  previous image that has working native login.

## Sign-in through ts-auth

The dashboard is a confidential client. Its native pages redeem passwords and
emailed codes through ts-auth's headless API; SSO uses authorization code with
PKCE. Service discovery and OCI Vault must automatically provide the trusted
issuer, client credential and exact callback bindings.

**Current gap:** the runtime still needs `TS_AUTH_*` inputs and development
cookie/issuer overrides. Automatic provisioning is not established here.
Production requires HTTPS and secure cookies. Discovery must identify the
exact regional issuer, advertise S256 and match registered redirect/logout URIs.
The global auth proxy is not itself a regional issuer. Never weaken issuer
verification, CSRF or redirect allowlists to make a missing binding work.

The existing authenticated flow:

With the variables set:

- `POST /login` and `POST /login/code/verify` redeem ts-auth's password and
  email-code grants at its token endpoint, with the end user's IP address as
  `ip_address`. `/login/code` asks ts-auth to email a sign-in code,
  `/reset-password` a reset code. Every step reads the same whether or not
  the address has an account, and the dashboard's throttle limits attempts
  and mails per client and per address before ts-auth's own limits.
- Registration (`POST /onboarding`) asks ts-auth to email a code that
  verifies the address; entering it, with the sign-up's pending token from
  an HttpOnly cookie, opens the account. The local password JSON API
  (`POST /api/v1/auth/password/login`) is disabled.
- `/login`, `/login/code`, `/onboarding` and `/reset-password` always render
  dashboard forms. Submissions use ts-auth's headless API through ts-go;
  API failures stay on the native pages. Discovery advertising headless
  grants does not control whether these forms appear. `/login/sso` is the
  only explicit hosted sign-in redirect.
- The verification page's outline **Resend code** button becomes available
  after 60 seconds. A separate persisted throttle enforces the same limit
  per address across dashboard replicas. Since ts-auth requires the original
  password to issue another sign-up code, the request is held in an
  authenticated AES-GCM encrypted HttpOnly cookie for up to one hour, bound
  to the pending token. It is removed after successful verification. The
  confidential client's secret derives the cookie key, so rotating that
  secret expires pending resend requests. If the request exceeds the retry
  cookie's size limit, the emailed code still verifies the account; the page
  offers account creation again to request another code. Without JavaScript,
  the native resend form stays usable and the server enforces the minute.
- `/login/sso` redirects to ts-auth with the scopes `openid email profile
  offline_access`, a PKCE S256 challenge, `state` and `nonce`. ts-auth returns
  to `/login/callback`.
- Signing out revokes the session's refresh token when discovery lists a
  `revocation_endpoint`, and ends the ts-auth browser session through its
  `end_session_endpoint`, with `post_logout_redirect_uri` set to the
  dashboard's origin plus `/login`. Without that endpoint, or with ts-auth
  unreachable, it goes to `/login?prompt=login`, whose single sign-on asks
  ts-auth for credentials again.

The account for a sign-in is found by ts-auth subject, else linked by verified
email, else created. The ID token must carry `email_verified: true`. The
subject is kept as `User.AuthSubject`; system database Management calls use
the signed-in user's ts-auth access token.

Sessions stay in the session store. Each is rechecked with its refresh token
when ts-auth's access token expires, at most once every 5 minutes. A refused
refresh token ends the session; while ts-auth is unreachable the session
stays and the recheck is retried a minute later. A session ts-auth issued no
refresh token for is not rechecked and lasts its full lifetime. A scope
change rotates the session only while the old one is still live, carrying its
refresh token over.

Accounts created through ts-auth have no dashboard password, so actions that
confirm one (approving a CLI device, creating a personal access token,
changing the password or email) refuse them until step-up re-authentication
through ts-auth is added.

Without these variables nothing changes: local password sign-in and the demo
backend work as before.
