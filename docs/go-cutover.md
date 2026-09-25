# Go runtime cutover and C# retirement

**Status: IN PROGRESS.** This is a cutover checklist, not evidence that its
acceptance gates have passed.

Review baseline: PR #74, rewrite head `835fe654ae1827d0db2104ce536c0b13c241cfb3`,
synced with `main` at `53a061d9ec68be9dfdeb28ce2e3ef27270dec2be`. Re-record the
merged commit and gate evidence after merge.

## What this cutover contains

The deployable application in this checkout is the Go server in `cmd/dashboard`.
It renders with templ, uses the Datastar Go SDK, and serves the Datastar v1.0.4
browser bundle with Rocket beta.2. The application uses the first-party
`terrascale.tech/sdk` Go module pinned to ts-go commit
`3088ef2e3bd0cfbd22c980c029b2b745b82d1eff`; the dependency is vendored, so
builds do not use a sibling C# or SDK checkout. Multiple CSS source files under
`web/assets` are bundled by Lightning CSS into
`web/static/css/terrascale.css`.

The supported account mode is `TS_DASHBOARD_PRODUCTION_DEMO=1`. It is required
by startup, uses process-memory accounts and sessions, and resets them when the
process restarts. Production is configured to keep one Fly machine warm;
preview machines may stop while idle and start with an empty store. A cutover
therefore forces a fresh login and account state in the Go runtime. No C# to Go
account or session migration is part of this work.

The demo integration boundary must remain clear. Both the former C# demo and
the Go demo present sample external SDK credentials and preview MCP/SDK/API
connection surfaces; these are not proof of live external connections. The Go
home page's MCP configuration is preview UI. The real TypeScript SDK browser
journey builds the pinned test-only `ts-typescript` snapshot and talks to a
loopback fixture under `tests/integration/fixture/`; it verifies the Go
dashboard flow against that fixture, not against a deployed TerraScale API.

Authentication parity is intentionally limited in demo mode. The former C#
demo's fake passkey sign-in shortcut, which granted an anonymous administrator
session, is not reproduced. Password login to the seeded Mario test
administrator remains available. This is a shared public test account: its
synthetic account, organization, project, and database data can be changed by
anyone using that login, so it does not provide private tenant isolation and
must not hold private data. The state resets when the Go process restarts.
Passkey and MFA profile entries are demo records only; they cannot authenticate
or protect sign-in. Google and GitHub sign-in are unavailable in this mode.

Live persistence, a production ts-auth provider, and real Stripe checkout are
outside this mandated demo cutover. The Go app currently refuses non-demo
startup because live persistence is not configured, and Stripe is disabled in
demo mode. Passing the demo gates below must not be described as full
non-demo/runtime parity or completion of those integrations.

## Current implementation and acceptance status

Every row remains **IN PROGRESS** until the implementation is merged and its
evidence is recorded. Candidate code in the shared worktree is not a passed
gate.

| Work area | Status | Evidence still required |
| --- | --- | --- |
| Three-tab UI | IN PROGRESS | Review the merged routes, keyboard/accessibility behavior, and responsive browser coverage. |
| 404 handling | IN PROGRESS | Verify unknown paths return the intended 404 page without changing existing route behavior. |
| SDK test fixture | IN PROGRESS | Pass the local fixture account-to-database journey with the pinned TypeScript SDK; record that it is test-only. |
| Invoice views/flows | IN PROGRESS | Review the implemented flow and its focused Go and browser assertions. |
| SQL analysis | IN PROGRESS | Review the implementation and pass focused Go and browser checks for supported and rejected inputs. |
| Index flows | IN PROGRESS | Review index operations and pass focused authorization and browser coverage. |
| Estate/admin surface | IN PROGRESS | Verify owner/admin access and denial behavior in the merged routes and browser suite. |

## Cutover gates

Do not mark a gate passed without its result from the candidate merged commit.

| Gate | Required evidence | Status |
| --- | --- | --- |
| Generated source and lint | `make check-generated` and `make lint` | PENDING |
| Fast suite | `make test-fast` | PENDING |
| Browser and SDK suite | `make test-slow`, including the loopback SDK fixture | PENDING |
| Release build | `make build` from the repository, using vendored Go dependencies | PENDING |
| Preview smoke | Readiness, login, account creation, onboarding, and static assets on the PR preview | PENDING |
| Cold-start/reset behavior | Confirm a stopped preview wakes ready and that demo state resets as documented | PENDING |
| C# retirement review | Review residual source/dependencies and retain a known-good rollback target before removal | PENDING |
| Rollback preparation | Record a known-good release and its configuration, and confirm the restore path before cutover | PENDING |

## Reproduce locally

From the repository root, install the pinned toolchains and JavaScript tools,
then use these commands as needed:

```sh
mise install
npm ci
make dev PORT=5481
make check-generated
make lint
make test-fast
make test-slow
make build
```

`make dev` binds the server to `0.0.0.0:5481`, enables
`TS_DASHBOARD_PRODUCTION_DEMO=1`, and runs the Go/template and CSS watchers.
`make test-slow` runs the Playwright suite and the local test-only SDK fixture.
`make build` bundles the CSS and produces the optimized Go executable. These
commands do not exercise live persistence, live Stripe, or a production ts-auth
provider.

## Safe retirement and rollback preparation

Before removing C# sources or package references:

1. Have a human update the stale C#/RazorSlice/ts-dotnet stack guidance in
   `AGENTS.md` as a separate repository-maintenance task. The Go direction
   supersedes that guidance for this work; the stale text does not block Go
   implementation or operation. The file is human-maintained, so agents must
   not edit it.
2. Pass and record every cutover gate against the merged Go revision. Confirm
   which external authentication and payment features remain out of scope.
3. Tell affected users that the Go demo cutover forces a fresh login and does
   not carry C# accounts or sessions across. Do not promise a data migration:
   none is defined by this work.
4. Before cutover, record the last known-good release and deployment
   configuration and verify the platform's rollback procedure. Keep the
   previous runtime/source available until Go preview and production smoke
   checks are accepted. Decide how to handle accounts created after cutover if
   rollback is needed; this document does not invent reconciliation behavior.

No source deletion or deployment is authorized by this checklist. After merge,
replace the pending statuses only with the actual CI and preview results,
including the tested commit and links to useful logs. Keep the non-demo and
external-provider limitations visible until they have separate implementation
and acceptance evidence. Keep the shared-account authentication limitations
visible anywhere the demo login is documented.
