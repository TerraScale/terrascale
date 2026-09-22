# TerraScale.Dashboard

C# port of the TerraScale dashboard (ts-dashboard), 1:1 in design, pages,
and features. Dark-only design system v1 rendered from a single CSS file.

See `AGENTS.md` for the full conventions (feature slices, one type per file,
one endpoint per file, CI/deploy).

## Stack

- **C# / .NET 10** with **Razor Slices** templates (`src/TerraScale.Dashboard/Features/*/**.cshtml`)
- **TerraScale.Endpoints** — every route is an endpoint class, one per file,
  discovered at startup (local `ts-libs` checkout preferred, vendored
  `src/TerraScale.Dashboard/nuget/` package fallback)
- **terrascale.css** — the whole design system in one stylesheet:
  tokens, base, components, and the dashboard shell (`src/TerraScale.Dashboard/wwwroot/css/terrascale.css`)
- **TerraScale.Datastar** (`wwwroot/js/datastar.js` and `src/TerraScale.Datastar/`) — the front/back
  communication layer; slices declare `data-*` attributes, and the dashboard
  owns the server-side SSE and signal implementation. The local project
  replaces the upstream `StarFederation.Datastar` package and keeps its MIT
  attribution in `src/TerraScale.Datastar/THIRD_PARTY_LICENSE.md`. Signal
  payloads can use `JsonTypeInfo<T>` overloads from an application-owned
  `JsonSerializerContext` to avoid runtime serializer metadata discovery.
- Server-rendered SVG charts (`Shared/Components/Charts.cs`) and lucide icon set (`Shared/Components/Icons.cs`)
- **TerraScale.DotNet** — the workspace prefers the sibling `ts-dotnet` SDK
  checkout; standalone Docker and CI builds restore the vendored SDK package
  from `src/TerraScale.Dashboard/nuget/`. Set `TERRABASE_URL` (or
  `TERRASCALE_API_URL`) to enable the live TerraBase engine page and
  `TERRABASE_API_KEY` (or `TERRASCALE_API_KEY`) when the member requires
  authentication.

## Layout

- `src/TerraScale.Dashboard/Features/<Feature>/` — each product area owns its
  pages and supporting code. Page files live together under `Pages/<Page>/`;
  action handlers live under `Endpoints/`; domain records under `Models/`;
  rendered data under `ViewModels/`; feature logic under `Services/` or
  `Presentation/`; and shared-store partials under `Stores/`.
- `src/TerraScale.Dashboard/Shared/` — reusable rendering, shell, navigation,
  components, models, and the shared `DashboardStore` state.
- `src/TerraScale.Dashboard/Infrastructure/` — authentication, configuration,
  persistence, and TerraBase integration.
- `src/TerraScale.Dashboard/wwwroot/css/terrascale.css` — the single-file CSS framework
- `TerraScale.Dashboard.slnx` — solution at the repo root

## Run

```
dotnet run --project src/TerraScale.Dashboard
# open http://localhost:5000 → redirected to /login
# demo credentials: mario@terrascale.tech / terrascale (development defaults to demo; production requires TS_DASHBOARD_PRODUCTION_DEMO=1)
```

Live account worlds are persisted through TerraBase change-token writes. If two
dashboard instances update the same world concurrently, the losing request
returns HTTP 409 `state_conflict` so the operator can reload before retrying;
the service never silently overwrites the winning state.

## Modes

The dashboard defaults to the in-memory demo backend. Supplying live identity
and state settings selects the live backend:

- **demo** (default in development): everything in-memory — the seeded
  demo account (mario@terrascale.tech / terrascale), instant onboarding,
  Playwright-verified v1 parity. Production requires the explicit
  `TS_DASHBOARD_PRODUCTION_DEMO=1` acknowledgment.
- **live**: ts-auth (the ts-dotnet SDK's auth service) owns identity —
  registration creates real users, sign-in walks the hosted form → OIDC
  authorize (PKCE) → token exchange, sessions validate through RFC 7662
  introspection and refresh through the rotating refresh grant. Account
  state persists as documents in Terrabase through the SDK with
  change-token optimistic concurrency (409 `state_conflict` on races), and
  database creates provision real Terrabase databases. Requires
  `TS_DASHBOARD_IDP_ORIGIN`, `TS_DASHBOARD_IDP_INSTANCE`,
  `TS_DASHBOARD_IDP_CLIENT_ID/SECRET`, `TS_DASHBOARD_IDP_MANAGEMENT_KEY`,
  `TS_DASHBOARD_PUBLIC_ORIGIN`, and `TS_DASHBOARD_STATE_URL/API_KEY/DATABASE`.
  Loopback HTTP origins are accepted outside production for local stacks.

Fly deployments use the checked-in `fly.toml`, which sets
`TS_DASHBOARD_PRODUCTION_DEMO=1`. With no live provider settings, this keeps
the production and pull-request preview apps on the seeded in-memory demo
backend.

That demo backend keeps accounts, sessions, and the onboarding wizard in
process memory, so it must run as **one warm machine**: the deploy workflows
pass `--ha=false` and `fly scale count 1`, and `fly.toml` disables autostop
(`auto_stop_machines = "off"`, `min_machines_running = 1`). A stopped machine or
a second machine answers with an empty store, which strands onboarding at
`/login` and rejects the account that was just created.

## Stripe sandbox

The dashboard owns Payments and Stripe management. With no Stripe settings the
billing surfaces keep the in-memory local payment setup; as soon as the sandbox
settings below are present, the same links start real **test-mode** Checkout and
Billing Portal sessions and the webhook projects subscription state onto the
organization.

Configuration comes from `Payments:Stripe:*` (environment variables use the
deployed `Payments__Stripe__*` spelling). During development the app also reads
a gitignored `.env.local` file (`DotEnvFile`); real process environment
variables always win over the file, and the loader is a no-op outside
Development. Copy `.env.stripe.example` and fill in the sandbox values, then:

```
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/TerraScale.Dashboard
```

| Setting | Purpose |
| --- | --- |
| `SecretKey` | Sandbox (`sk_test_…` / `rk_test_…`) secret key. Required to enable Stripe. |
| `Mode` | `test` (default) or `live`. A live key must use `live`, a test key `test`. |
| `WebhookSecret` | Signing secret of the webhook endpoint that delivers to this app. |
| `ApiVersion` | Optional. Pins the `Stripe-Version` header; unset uses the account default. |
| `PublicOrigin` | Public HTTPS origin Stripe redirects back to (Tailscale Funnel or Fly app). |
| `{Plan}{Interval}PriceId` | `Starter`/`Pro`/`Business` × `Monthly`/`Annual`. Only configured pairs are offered. `Scale*` is accepted as the legacy name for Business. |

Flow: `/billing/checkout` creates the hosted Checkout session and redirects;
Stripe returns to `/billing/checkout/return`, which confirms the session before
sending the customer back to the originating page; `/billing/portal` opens the
Billing Portal; `POST /api/auth/stripe/webhook` verifies the HMAC signature and
projects `checkout.session.completed`, `customer.subscription.*`, `invoice.paid`,
and `invoice.payment_failed`. The webhook is anonymous by design — Stripe cannot
present a dashboard session — and is authenticated by the signature instead.

The sandbox is refused in production unless `TS_DASHBOARD_PRODUCTION_DEMO=1` or
`Payments:Stripe:EnableTestModeInProduction=1` acknowledges it. `.env.local` is
gitignored and excluded from the Docker build, so deploy environments must
provide the settings as real secrets (`fly secrets set`).

### Sandbox setup

1. Copy `.env.stripe.example` to `.env.local` and fill in the sandbox secret key.
2. Confirm the price ids match the sandbox products (Starter, Pro, Business).
3. Expose the app over public HTTPS and create the webhook endpoint for
   `https://<origin>/api/auth/stripe/webhook` with the events listed above; put
   its `whsec_…` in `WebhookSecret`. The Stripe CLI alternative is
   `stripe listen --forward-to <origin>/api/auth/stripe/webhook`.
4. Create the default Billing Portal configuration in the sandbox (Stripe
   Dashboard → Settings → Billing → Customer portal) or the Portal session call
   fails with `No configuration provided`.

## CSS delivery

Edit only `src/TerraScale.Dashboard/wwwroot/css/terrascale.css`. The Docker build
uses Lightning CSS to minify it and replaces the published copy at the same
`/css/terrascale.css` URL. Development keeps the readable source; the runtime
image contains one stylesheet and needs no Node installation. Minifier errors
or warnings fail the build.

For a publish outside Docker, run `npm ci`, publish the app, then run
`npm run build:css -- <publish-directory>/wwwroot/css/terrascale.css`.
`npm run build:css` alone writes a preview to `obj/assets/terrascale.css`.

ASP.NET Core negotiates Brotli or gzip from `Accept-Encoding` for supported
content types over HTTP and HTTPS, before serving static files. Datastar's
`text/event-stream` responses are excluded by the default MIME type list.

## Tests

- `tests/TerraScale.Dashboard.FastTests` — TUnit, the user-blocking critical
  paths; seconds to run, run it before every push.
- `tests/TerraScale.Dashboard.SlowTests` — TUnit, methodical: every page, every
  mutation, plus the live stack (embedded Terrabase + real ts-auth) when
  the sibling `terrabase/` and `ts-auth/` checkouts exist; the live
  fixtures compile out otherwise.
- `npx playwright test` — browser E2E (demo mode).
- The shared harness lives in `tests/TerraScale.Dashboard.Testing`
  (`DashboardTestApp` boots the whole endpoint pipeline in-memory).

## Page pattern

Each page keeps its endpoint, model, and Razor slice together. For example,
the databases page is organized as:

```
Features/Databases/Pages/Databases/
  DatabasesPageEndpoint.cs
  DatabasesPageModel.cs
  DatabasesSlice.cshtml
```

Use `Models` for feature state and domain records, `ViewModels` for display
rows/cards/labels, and `Contracts` only when a request or response type crosses
an HTTP or service boundary. Do not create empty category directories.

```csharp
public sealed class DatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<DatabasesSlice, DatabasesPageModel>(store, "/databases")
{
    protected override DatabasesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
```

The slice opens with `@ShellHtml.Top(Model.Shell)` and closes with
`@ShellHtml.Bottom(Model.Shell)`; everything between is the page.
