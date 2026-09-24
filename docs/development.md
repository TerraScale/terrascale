# Go dashboard development

The repository pins Go 1.27.1 in `.mise.toml`. Use Node.js 24 with npm for the
Lightning CSS and Playwright tooling. At the repository root:

```sh
mise install
npm ci
```

## Local server

`make dev PORT=5481` binds the dashboard to `0.0.0.0:5481`, enables the
in-memory demo backend, and starts two development watchers: Air rebuilds Go
and embedded templates/assets, while `npm run watch:css` rebuilds the single
stylesheet. The app sends stylesheet-change events on `/__dev/events`; the
browser reload script listens only when `DASHBOARD_DEV=1` is set. When Air is
not installed, the script installs pinned Air v1.67.4 with `go install`; that
development-tool bootstrap needs access to the Go module proxy. The dashboard
itself builds from vendored modules. Stopping the foreground `make dev`
process with Ctrl+C terminates Air, its app child, and the CSS watcher.

`DASHBOARD_DEMO_ADMIN_EMAIL` and `DASHBOARD_DEMO_ADMIN_PASSWORD` seed the local
test administrator. The development script supplies test-only defaults; set
both variables to replace them. Do not use those defaults for shared or
production environments.

Use `make serve PORT=5481` for a plain HTTP server without Air or CSS watching.
Set `DASHBOARD_E2E_BASE_URL` when Playwright should reuse an already-running
server instead of starting its configured local server.

## Build and tests

```sh
make lint       # gofmt check and go vet
make css        # bundle web/assets into web/static/css/terrascale.css
make test-fast  # all Go tests with -race
make test-slow  # pinned TypeScript SDK build and Playwright suite
make build      # CSS bundle and optimized bin/dashboard-release executable
```

The TypeScript contract test uses the test-only snapshot under
`tests/integration/sdk/`, pinned to upstream `ts-typescript` commit
`d1021878d0d0dfeb5639ea3dd0f1ed7f0a9eee19`. `make test-slow` installs the
snapshot's locked public npm dependencies, builds its public entrypoint, then
runs Playwright. The fixture in `tests/integration/fixture/` starts only for
that test and does not enter production application wiring.

The app and Docker build use `GOFLAGS=-mod=vendor`. `vendor/PROVENANCE.md`
records the source commit for the vendored `ts-go` package. Do not replace this
with a sibling checkout path in `go.mod`; standalone CI and Docker builds must
work from the repository contents.

## Fly runtime

`fly.toml` configures the production app for one warm machine. Its process-local
accounts and sessions must remain on one machine and do not survive restarts.
`fly.preview.toml` configures one machine per preview and allows it to stop
while idle, so preview accounts are reset when it wakes. Both configurations
probe `GET /health/ready` and set `TS_DASHBOARD_PRODUCTION_DEMO=1`.

The Forgejo production workflow first runs lint, fast Go tests, and the full
Playwright/SDK suite. The preview workflow deploys one machine and then runs
the same slow suite against its URL. No live deployment is required for local
development or tests.
