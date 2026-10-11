# TerraScale.Dashboard migration

The requested end state is one C# application project, `TerraScale.Dashboard`,
in `TerraScale.Dashboard.slnx`, using RazorSlices, Datastar, the official
ts-dotnet SDK and persistent TerraBase state. The existing design system,
features, ts-auth authorization, Stripe integration and MCP surface must be
preserved. Production must dogfood the platform it offers to customers.

**The migration is incomplete.** The existing Go deployment remains the
production implementation while its feature slices are ported. The C# host
currently renders the signed-out authentication and illustrated 404 surfaces;
its authenticated root returns 503 until the signed-in feature surface is
ported. It must not replace the Fly deployment yet.

## Implemented foundation

- One application project in the named `.slnx`; .NET 10, RazorSlices 0.12.1.
- Exact existing Lightning CSS sources and generated stylesheet, self-hosted
  Datastar 1.0.4, Monaco bundles, fonts, flags and committed SVG brand/map files.
  Asset fingerprints retain the first six SHA-256 bytes used by the Go host.
  The browser build reads Datastar integrity pins from C# now.
- Razor slices for password sign-in, email-code sign-in, password reset,
  password reveal controls and the illustrated 404. The original auth panel
  markup, copy, classes and map placement are carried over.
- CSRF validation, same-origin redirect handling, nonced CSP, no-store auth
  responses and fingerprint-aware static caching.
- ts-auth headless HTTP adapter against its existing grant contract; userinfo,
  token introspection and revocation go through ts-dotnet. The SDK does not
  have headless-grant methods at the pinned revision.
- SDK-backed TerraBase document state boundary. Session bearer tokens are
  hashed for document keys; identity and access/refresh tokens are encrypted
  with AES-GCM before storage. Conditional deletion uses document versions.
  No application memory store, seeded account or auth bypass is registered.
- Datastar SSE element/signal framing and a per-worktree, all-interface
  `dotnet watch` launcher. Development uses a same-origin SSE stream to refresh
  pages after C#/Razor hot reload or process restart and update the stylesheet
  after CSS changes, including when accessing the host through Tailscale.

These are implementation and isolated-test results, not live producer
qualification. The committed SDK package is a local candidate with recorded
source/digest provenance; see [the SDK pin](../vendor/dotnet/README.md).

## Commands

Run local commands with low CPU priority, including servers/watchers:

```sh
nice -n 19 make dotnet-build
nice -n 19 make dotnet-lint
nice -n 19 make dotnet-test-fast
nice -n 19 make dotnet-dev
# Pass the URL printed by the development host, including its worktree port:
nice -n 19 node tests/dotnet/browser.mjs http://HOST:PORT
nice -n 19 node tests/dotnet/hot-reload.mjs http://HOST:PORT
```

The fast suite is a file-based .NET test binary using `TestServer`; it does not
add another committed application/test project. Its state, identity and auth
test doubles exist exclusively in `tests/dotnet/`, excluded from application
compilation. It checks CSRF, local redirects, encrypted sessions shared across
two hosts, revocation/expiry, reset, assets, 404 and Datastar SSE. The browser
suite checks desktop/mobile rendering, password behavior, navigation and
WCAG 2.2 AA rules. Screenshots are written to ignored `test-results/dotnet/`.
It does not prove signup-to-TypeScript-SDK parity or live TerraBase persistence.
The hot-reload check temporarily changes C# copy and CSS, waits for those
changes to reach an open browser, then restores and verifies the original
source and appearance. Run it in the migration worktree with `dotnet-dev`.

## Runtime integration gates

The C# consumer currently reads machine identity from the fixed path
`/run/secrets/terrascale/dashboard.json`. This is a new consumer boundary,
**not an existing, verified OCI delivery contract**. Automatic provisioning,
delivery and rotation must be implemented/qualified before deployment. There
is no endpoint override, demo selection or manually configured fallback.

The descriptor contains the state-project scope and service credentials,
ts-auth instance/client registration and the shared session-encryption key;
`DashboardCredentials` defines its exact schema. The state database is named
`dashboard`, with the `sessions` collection. Automatic bootstrap of this
first-party tenant/project/database/collection is still required. Authentication
and database endpoints are resolved through Atlas, without per-region switches.
The existing `/v1/discovery` region catalog is being used for the Documents
origin while SDK-owned Documents discovery (TS-507) is outstanding; this
adapter needs qualification against the producer before it is a production
contract. No catalog entry is sufficient evidence that the Documents API
actually serves there.

On the initial migration validation, `atlas.terrascale.tech` did not resolve
from this machine, and the pinned TerraBase release URL in ts-dotnet's testing
helper returned HTTP 404. No matching release binary was found locally. Live
service authentication, persistent-state restart tests and SDK interoperability
therefore remain unverified; no live test is reported as passing.

## Remaining feature parity

The Go source and tests remain the authoritative inventory. Port each area
into its own `Features/` folder and reuse the corresponding CSS/classes;
do not replace the product with generic CRUD or fabricated operational data.

| Area | Work still required |
| --- | --- |
| Authentication | Signup and verification/resend, SSO with PKCE/state/nonce, callback, refresh coordination, MFA/session flows, CLI/device authorization, account linking and platform-admin claims |
| Onboarding | Persisted organization/project setup, placement and billing steps, accessible region inputs, complete account creation flow |
| Shell and home | Sidebar/context/account menus, notifications dialog, status notices, overview/connect tabs, scroll/progress behavior, real activity/region/replication summaries |
| Organizations/projects | CRUD, switching, members, roles, invitations, ownership transfer, deletion/leave, audit/retention, plan/billing permissions |
| Databases/data | Database lifecycle/detail tabs, placement, branches, collections/documents, SQL/editor workflows and streaming results |
| API keys/access | Scoped API keys, M2M clients, personal access tokens, revocation and enforcement |
| Authentication console | Users, sessions, MFA/passkeys, SSO connections, directories, OAuth clients, JWT templates, webhooks and instance settings via ts-dotnet |
| Notifications/profile/settings | Persisted inbox/read actions, profile and settings surfaces, security and token management |
| Payments/plans/subscriptions | Official Stripe.net integration, checkout/portal, signed webhooks, invoices, spending controls, internal billing, plans and query subscriptions |
| Analytics/usage/status | SDK-backed metrics, usage and limits, chart rendering, regional latency and external status integration; producers own these measurements |
| Replication/backups | Existing lifecycle, policies, retention and restore workflows using producer APIs |
| Administration/audit | Users, organizations, projects, databases, estate, usage, plans and authorization/audit enforcement |
| MCP | Protocol/auth/discovery and the complete existing tool/resource/action surface, sharing authorization and services with browser routes |
| Deployment/tooling | C# Fly image, health/readiness aliases, release/CI checks, stack launcher and removal of superseded Go runtime/vendor/tooling |
| Qualification | Official SDK distribution, actual TerraBase/ts-auth integration, restart/cross-instance persistence, complete signup-to-ts-typescript-SDK journey, full fast/slow suites and several before/after screenshots |

The final completion audit must prove all of these requirements against running
behavior and current source. A green foundation suite is not feature-parity
evidence. Keep `AGENTS.md` and `CLAUDE.md` untouched; the user's explicit C# and
ts-dotnet request supersedes their older Go-specific stack instructions.
