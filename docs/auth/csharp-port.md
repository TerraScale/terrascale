# C# port (TS-424)

ts-auth is a C# service on .NET 10. Epic TS-424 (ku.mariogk.com) tracks the
port. The Rust workspace was removed in TS-442; its source in Git history
before that change is the behavioural reference for each port stream, and
`docs/parity.md` tracks what has landed.

## Layout

| Path | Contents |
|---|---|
| `src/TerraScale.Auth/` | ASP.NET Core host. Features live in `Features/<Area>/`; each endpoint is one TerraScale.Endpoints class. |
| `src/TerraScale.Auth.Contracts/` | Persisted models and the `{"error":{"code","message"}}` envelope (`ApiException`). |
| `src/TerraScale.Auth.Security/` | Cryptographic primitives (port of `auth-security`). |
| `src/TerraScale.Auth.Store/` | TerraBase native-v1 adapter on ts-dotnet (port of `auth-store`). |
| `tests/TerraScale.Auth.Fast/` | Blocking integration suite (TUnit + Playwright). |
| `tests/TerraScale.Auth.Slow/` | Non-blocking integration suite. |
| `tests/Shared/` | Harness source linked into both suites: the app on a loopback Kestrel port, Chromium launcher, test doubles. |

There are no unit tests. Tests start the real application with
`AuthTestServer.StartAsync` and talk to it over HTTP. Test-only routes and
doubles are added through that call and exist only in the test process.

The sibling `ts-dotnet` and `ts-libs` checkouts are project references. Locally
they are found at `../ts-dotnet` and `../ts-libs`, or under
`~/Projects/TerraScale/`; pass `-p:TerraScaleRoot=<dir>` to use another
location. CI checks out the revisions pinned in `.forgejo/workflows/ci.yml`
into a job-owned directory outside the repository.

## Commands

Run them at low CPU priority locally:

```sh
nice -n 19 make check       # format, build (analyzers as errors), NuGet audit, OpenAPI lint, fast suite
nice -n 19 make test-slow   # slow suite
nice -n 19 make dev         # dotnet watch, published over HTTPS on the tailnet
```

`make dev` picks a free loopback port and a free tailnet HTTPS port for this
worktree, prints the `https://<machine>.<tailnet>.ts.net:<port>` origin, and
removes only its own `tailscale serve` mapping when it stops. Put local
settings in `.env` (gitignored); without it, throwaway Development values are used.

## Configuration changes from the Rust service

The release image is the self-contained executable on the chiseled
`runtime-deps` base (see README, CI).


All `TS_AUTH_*` settings keep their names and defaults, with these exceptions:

- **`GET /metrics` and `TS_AUTH_METRICS_KEY` are removed.** This is an
  intentional breaking change. Metrics, traces and logs are pushed over
  OpenTelemetry OTLP when the standard `OTEL_EXPORTER_OTLP_ENDPOINT` (and
  optionally `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_HEADERS`) is set. A
  collector can expose a Prometheus scrape endpoint if one is needed.
- **`TS_AUTH_SENTRY_DSN`** enables error reporting to GlitchTip through the
  Sentry SDK, for errors only. It is a deployment secret and must never be
  committed. Events are scrubbed before they leave the process: only
  non-sensitive request headers are kept; cookies, query strings, bodies, the
  user and the client IP are dropped; sensitive keys in tags, extras and
  breadcrumbs are masked. The fast suite checks this against a fake collector.
- Framework request logging (`Microsoft.AspNetCore.Hosting.Diagnostics`,
  `System.Net.Http.HttpClient`) is limited to warnings, because those
  categories log full URLs, query strings included.

## Readiness until the native-v1 store lands

`/readyz` returns 503 until stream B (TS-428) provides a real TerraBase probe.
The placeholder `UnavailableStorageHealth` fails closed by design.

## OpenAPI

TerraScale.Endpoints serves the document at `/openapi/v1.json` and Scalar at
`/scalar`. Scalar's JavaScript is served from the package, and its default
fonts are turned off, so the page makes no third-party requests. The document
transformers in `Hosting/OpenApiSetup.cs`:

- set the server to the configured public origin, never the request's Host header;
- apply the security scheme each route enforces, and mark public operations `security: [{}]`;
- define only the schemes that are actually used;
- require a description for every tag.

`make openapi-check` lints the served document with Redocly.
