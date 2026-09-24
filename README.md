# TerraScale Dashboard

This worktree contains the Go and Datastar dashboard runtime. It uses Go 1.27.1,
server-rendered HTML templates, the official Datastar Go SDK, and one
lightningcss stylesheet. The Go commands and workflows below target this
runtime.

## Develop

Install Go 1.27.1 and Node.js 24, then install the root JavaScript dependencies:

```sh
mise install
npm ci
```

Start the hot-reload development server with:

```sh
make dev PORT=5481
```

The server binds `0.0.0.0:5481`, sets `TS_DASHBOARD_PRODUCTION_DEMO=1`, watches
Go and embedded template/assets through Air, and watches the CSS sources with
lightningcss. CSS changes are streamed to the browser over the dev-only
`/__dev/events` endpoint. The default local test administrator is
`e2e-admin-seed@example.test`; override both
`DASHBOARD_DEMO_ADMIN_EMAIL` and `DASHBOARD_DEMO_ADMIN_PASSWORD` together when
needed. `make serve PORT=5481` runs the HTTP server without development
watchers. See [development commands](docs/development.md) for test and build
details.

All accounts, sessions, and onboarding data in this demo mode live in memory.
They reset when the process restarts. Production therefore keeps one warm Fly
machine, and each preview runs one machine that may stop while idle.

## Checks

```sh
make lint
make test-fast
make test-slow
make build
```

`test-fast` runs the Go test suite with the race detector. `test-slow` builds
the pinned, test-only TypeScript SDK snapshot and runs the Playwright suite,
including the real SDK account-to-database contract journey. The snapshot is
under `tests/integration/sdk/` and records its upstream revision and file
hashes; it is not a production dependency. `make build` writes the optimized
executable to `bin/dashboard-release`. Browser tests can reuse an existing
server by setting `DASHBOARD_E2E_BASE_URL`.

Go dependencies, including the pinned `ts-go` SDK source, are vendored and
tracked with provenance in `vendor/PROVENANCE.md`. Builds use
`GOFLAGS=-mod=vendor`, so Docker and CI do not need a sibling SDK checkout.

## Runtime and deployment

The production and preview Fly configurations set
`TS_DASHBOARD_PRODUCTION_DEMO=1` and use `/health/ready` for readiness checks.
The Docker image builds the one-file CSS bundle, compiles the Go server against
the vendor tree, and runs it as a non-root user. CI runs lint, the Go test
suite, and browser/SDK tests before a production deployment. Pull-request
previews run the browser/SDK suite against the deployed preview app.

The app can receive an optional TerraScale API endpoint and access token through
`TS_API_BASE_URL` and `TS_API_ACCESS_TOKEN`. Current account and dashboard
state still use the in-memory production-demo store; restarting the process
resets that state.
