# Go runtime cutover and C# retirement

**Status: IN PROGRESS.** This is a cutover checklist, not evidence that its
acceptance gates have passed.

Integration baseline commit `75730298160e97cf2175cee873b5983ad6e3c84c`
includes `main` commit `53a061d9ec68be9dfdeb28ce2e3ef27270dec2be`.
The final PR head contains additional verified working-tree changes; the
baseline commit is not the final source identity. PR #74 remains open. A manual
merge sends a push to `main` and triggers the production deployment workflow,
so leave the PR open until the owner authorizes that release.

## What this cutover contains

The deployable application in this checkout is the Go server in `cmd/dashboard`.
It renders with templ, uses the Datastar Go SDK, and serves the Datastar v1.0.4
browser bundle. The application uses the first-party
`terrascale.tech/sdk` Go module pinned to ts-go commit
`430cb6624fa85ab22effc1a28d718db7d11297e1` from the public repository through
the versioned replacement in `go.mod`; the dependency is vendored, so
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
home page's MCP configuration is preview UI. The browser journey uses `ts-go`
against a loopback fixture under `tests/integration/fixture/`; it verifies the Go
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

## Implemented source matrix

The implementation slices below are present in the verified Go source. Source
review is clean. The full runtime and visual gate is active; no gate result is
claimed here until the operator reports it.

| Surface | Implemented source behavior and limits |
| --- | --- |
| Home charts | The seeded Mario account gets deterministic, explicitly labelled sample activity matching the C# `Random(42)` seven-day series. The 7-day Reads/Writes/Replication totals are 32,638 / 19,724 / 6,908. Fresh accounts get a flat zero baseline and no provider telemetry claim. |
| Usage | Overview, Billing, and Spending controls are three separate views. Mario gets labelled sample capacity rings and a three-series sample chart; fresh accounts have empty workload history when timestamped provider data is unavailable. Sample capacities are not canonical plan allowances or metered usage. |
| Plan labels | The Mario fixture uses canonical `Pro` (`plan_pro` / `pro`). Legacy `scale` is only an alias for canonical Business, not a separate plan and not a replacement for Pro. |
| Database detail | Five tabs are implemented: Overview, Placement, Backups, Branches, and Import and export. Configured region and local demo actions are shown; live placement, latency, replication, and generation telemetry are not available. The labelled 0 ms value is restricted to the Mario sample. |
| Estate | Regions, Hosts, and Operations are three owner-only tabs over local recorded data. Provider host inventory, availability, health, and live estate telemetry are unavailable; operation entries are records and do not change infrastructure. |
| Index views | Overview, Templates, Recommendations, and Migration are implemented. Mario's seeded `core-primary.orders` view shows a static sample snapshot at 8,640 / 12,480 (69%); it is not provider progress and cannot be advanced. Other accounts and collections show migration state as unavailable. |
| Profile and roles | Profile, Security, and Sessions are three tabs. The Profile view summarizes the active organization role and listed common capabilities; it is not an exhaustive authorization matrix. MFA and passkey entries remain non-authenticating demo records. |
| Invoices | The Billing view supports invoice filters and pagination over the dashboard's local records. The demo has no live Stripe invoices or provider-backed checkout. |
| SQL and SDK fixture | SQL analysis and index operations use the vendored `ts-go` adapter contract. The account-to-database journey uses `ts-go` and a loopback fixture, not a deployed TerraScale API. |
| 404 handling | Public and dashboard not-found routes render the Go-owned not-found page; compatibility routes remain in the Go server. |

## Final runtime and visual gate

**Status: LOCAL SOURCE, PREVIEW, AND DESKTOP VISUAL GATES PASS; REMOTE CI
PENDING.** Verification ran against a working-tree snapshot based on integration
commit `75730298160e97cf2175cee873b5983ad6e3c84c` (baseline tree
`2de640746a3d0f606ec8f3258297beb6fb282128`). Its tested application
source/config is identified by the 58-file manifest at
`/tmp/dsh198-final-app-source-manifest.sha256`
(SHA-256 `7c412faedcc65e1d0b36220495d01bf34bedebd0897822e4c1ebfa0225f9c4bf`).
`make generate`, `make css`, `make check-generated`, `make lint`,
`make test-fast` (Go race suite), `DASHBOARD_E2E_PORT=5625 make test-slow`
(23/23 browser and SDK tests), and `make build` passed. The release binary SHA-256
is `a433ffeee760bc273e39615010663f7f0dc385acc4910bc8d1b3fdde51809b6f`; the
170,288-byte CSS bundle SHA-256 is
`81209a05ef143ad74b52a3c62334783cf678e6755af8fd41cc9ada7043f15497`.
Static Fly configuration/workflow checks passed for the production target of one 512 MB,
2 shared CPU machine with 900 MB swap and one 1 GB volume, and preview at one
256 MB, 1 CPU machine with no swap or volume; the future three-machine
production setup remains commented out. The preview at
<http://mariogk-dev.tail057f.ts.net:5624> passed health, login, CSS, and HMR SSE
checks. No live Fly app was changed. Operator logs are under `/tmp`:
`dsh198-final-generate-3.log`, `dsh198-final-css.log`,
`dsh198-final-check-generated.log`, `dsh198-final-lint-3.log`,
`dsh198-final-{test-fast-2,test-slow-2,build-5}.log`
and `dsh198-preview-final-*`. The final desktop capture covered 27 states at
1440×900 with no image failures, JavaScript errors, failed requests, or
horizontal overflow; the expected public not-found response was the only
reported 404. Capture data is in
`/tmp/dsh-198-cutover-final/go/final-capture.json`. Independent image-pair
review compared 27 full-page Fly/Go desktop pairs at 1440×900 and found no
concrete visual defect. Its manifest is
`/tmp/dsh-198-cutover-final/pair-manifest.json` (SHA-256
`2ba9d1a8d1a735861cc0b704d3d6aaef1f77edea50c907f95f48299d04591931`). The Fly
30-day Usage selection is limited by its seven days of retained telemetry;
Go's 30-day chart uses labeled demo samples, so the values are not comparable
provider readings. Do not infer provider telemetry or non-demo integration
success from demo-mode results.

The committed production Fly configuration targets one warm machine: shared CPU 2x,
512 MB RAM, 900 MB swap, and one 1 GB Fly volume per machine. Auto-stop is off,
auto-start is on, and `min_machines_running` is 1. The deployment workflow sets
the active count to one and verifies the deployment. The volume is local to its
machine and does not persist the Go process-memory accounts or sessions.

The production workflow contains a commented future three-machine setup. Keep
it disabled until account and session state are shared. Each extra machine
would have its own volume; per-machine volumes do not share state.

PR previews use one 256 MB, shared CPU 1x machine, with auto-stop enabled,
auto-start enabled, `min_machines_running = 0`, and no configured swap or
volume. The preview workflow scales to one machine and checks the count and
absence of volumes. This is a workflow check, not a Fly-wide cap: manual or API
changes outside the workflow can change the machine count. A stopped preview
starts with empty process memory.

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
2. Record the final runtime and visual gate against the Go revision. Confirm
   which external authentication and payment features remain out of scope.
3. Tell affected users that the Go demo cutover forces a fresh login and does
   not carry C# accounts or sessions across. Do not promise a data migration:
   none is defined by this work.
4. Before cutover, record the last known-good release and deployment
   configuration and verify the platform's rollback procedure. Keep the
   previous runtime/source available until Go preview and production smoke
   checks are accepted. Decide how to handle accounts created after cutover if
   rollback is needed; this document does not invent reconciliation behavior.

This documentation does not delete source or deploy the app. Keep PR #74 open:
merging it triggers the production workflow. Update the final-gate paragraph
only with the operator's actual CI, preview, and visual results, including the
tested commit and links to useful logs. Keep the non-demo, external-provider,
and shared-account limitations visible until they have separate implementation
and acceptance evidence.
