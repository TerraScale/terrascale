# TerraScale.Dashboard migration

The requested end state is one C# application project, `TerraScale.Dashboard`,
in `TerraScale.Dashboard.slnx`, using RazorSlices, Datastar, the official
ts-dotnet SDK and persistent TerraBase state. The existing design system,
features, ts-auth authorization, Stripe integration and MCP surface must be
preserved. Production must dogfood the platform it offers to customers.

The active goal also requires **TUnit and E2E tests only** (user decision
2026-10-12). Tests exercise the running application through HTTP, MCP, the
official customer SDK and browser interactions. Isolated service, component
and unit suites must be replaced with E2E journeys; wrapping those isolated
assertions in TUnit does not satisfy this requirement. Demo providers belong
only in the test host. Live provider and TypeScript SDK qualification remains
a separate required E2E journey and must not be reported as passed by a demo.

## Current hosting (2026-10-22)

Production runs on OCI gru under ts-orch. The current contract is in
[OCI deployment](oci-deployment.md); the dated observations below are historical
and do not describe current hosting or secret delivery.

## Status (2026-10-12)

The Go source, its reference host and the strict visual parity journey were
removed. The C# application in `src/TerraScale.Dashboard` is the only
implementation. The seed fixture captured once from the Go application is
`tests/TerraScale.Dashboard.E2E/Fixtures/reference-workspace.json`.

Production is not deployed. Read-only checks at 18:09 UTC found: the retired deployment
`ts-dashboard` has no machines and no releases; ts-auth `/readyz` returned
503; `atlas.terrascale.tech` and `auth.terrascale.tech` are NXDOMAIN; and
ts-terrabase was started only in `sjc`. The C# image exits at startup because
its runtime secrets ([identity-descriptor.md](identity-descriptor.md)) are not
set. Persistence migration and live producer qualification remain open.

On 2026-10-12 the user accepted merging the implementation into GitHub
`main` without perfect parity. That acceptance does not qualify a production
cutover. The sections below are a dated log: run reports that mention the Go
application or strict visual parity describe the state at that time.

## Implemented behavior

- One .NET 10 application project, RazorSlices, Datastar and the original
  Lightning CSS sources, Monaco bundles, fonts, flags and file-backed SVG
  brand/map assets. Asset fingerprints retain the Go six-byte SHA-256 format.
- Sign-in only through ts-auth's hosted pages: authorization code with
  PKCE/state/nonce and signed ID-token validation, provider logout and
  persistent browser sessions. Sign-up, password reset, password change, MFA
  and CLI device approval live on ts-auth. Refresh coordination uses conditional
  writes. Browser sessions, independent credentials and refresh leases are
  encrypted; bearer lookup keys are hashed. Provider activity is checked on
  use. CLI approval and delivery are bounded and single-use.
- Account, organization and project context, onboarding and placement,
  organization/project creation and switching, invitations and membership,
  ownership transfer, audit filters/exports and provider-supported lifecycle
  operations. Role authorization is read freshly from the provider.
- The shared shell, account/context menus, notifications, profile and session
  management. Home overview, analytics, replication and status use producer
  adapters; missing producer measurements are shown as unavailable.
- Database creation/details/placement/branches, collection and document CRUD,
  versioned updates and JSON merge patches, schema, SQL, saved queries and
  history. API keys use the real SDK boundary.
- Personal access tokens with one-time plaintext display, hashed lookup,
  independent encrypted provider credentials and persistent revocation.
- Official Stripe.net checkout, customer portal and signed webhook handling.
  Customer ownership and checkout/subscription bindings persist in TerraBase;
  conditional reconciliation refetches authoritative Stripe state on races.
  The organization wizard binds checkout return to the authorized organization.
- Public internal-billing configure/report commands authenticate through
  ts-auth's device authorization grant and refresh signed administrator authority before privileged
  writes. Persisted payment reservations fence internal billing and Stripe
  checkout; a singleton binds the canonical internal organization. Monthly
  estimates use every project's actual producer usage, copied plan allowances
  and explicit rates. Incomplete measurements remain unavailable. Cap alerts
  use persistent organization/owner deduplication and recover interrupted writes.
  The original public command and monthly-report journeys have passed in the
  test host, including concurrency, browser/MCP and permission-change coverage.
  The final forced-refresh cap-change refusal and wrong document-key refusal
  passed the next compiled checkpoint. This does not qualify live billing.
- Official MCP server transport, stateless PAT authorization and feature tools
  sharing browser services and current organization permissions.
- Query-subscription navigation and persisted configuration metadata. The
  producer has no published delivery/management contract, so mutations return
  an explicit unavailable error rather than simulating a live subscription.
- Authentication-console routes retain the original navigation and forms.
  Unsupported customer-instance operations return an explicit 503 and disabled
  actions. The producer serves part of the project-tenant issuer/discovery
  contract, but the dashboard still lacks the customer management and scoped
  credential-delivery contracts needed by these forms. Fabricated instances
  or tenant credentials are not substituted. The Go demo's simulated records
  are not production contracts.
- CSRF, local redirect validation, CSP, no-store auth responses, static asset
  caching, Datastar SSE and an all-interface worktree-specific development
  watcher. Same-origin SSE refreshes Razor/C# pages and CSS over Tailscale.

These are implementation and test-host E2E results, not live producer
qualification. The committed ts-dotnet package is a local candidate with
recorded source and digest provenance; see [the SDK pin](../vendor/dotnet/README.md).

## Testing and demo

The final merge checkpoint on 2026-10-12 completed all 266 ordinary TUnit
E2E cases in 12 minutes 51 seconds: 265 passed, one failed, zero skipped.
Every functional journey passed. The sole failed test was the unchanged
strict visual comparison, which matched 56 of 110 states exactly; the two
additional Analytics differences reflect the current reporting copy and
separate replication directions. The full command therefore returns a
nonzero result; neither the visual assertion nor its reference was weakened.
The user accepted this known parity limitation for merging the current code.
Locked Release build, lint, SDK provenance, formatting and diff checks pass.
Reports are ignored local artifacts under `test-results/tunit/`, including
`final-merge-full-e2e.trx`, the 10-case reporting/authorization receipt and
the 27-case backup/readiness/webhook regression receipt. Live qualification
remains separate and failed as recorded under the runtime integration gates.

Run every local build, test, lint and watcher with low CPU priority:

```sh
nice -n 19 make build
nice -n 19 make lint
nice -n 19 make test-e2e
nice -n 19 make demo
nice -n 19 make test-hot-reload
nice -n 19 make test-live-readiness
nice -n 19 make perf
```

Use the demo URL printed by its launcher, including its worktree-specific
port. It binds all interfaces and supports the same Tailscale refresh path.
Demo sign-in goes through a test-only stand-in for ts-auth's hosted pages as `user@example.com`.
The launcher loads demo-only provider fixtures into a temporary test host and
runs it with the production route/service graph. All identity, state,
provider and Stripe protocol fixtures remain under
`tests/TerraScale.Dashboard.E2E/Demo`, excluded from application compilation.
Its Stripe transport refuses unknown external hosts;
checkout and portal are test-only local vendor pages.

The E2E runner is a separate test executable under
`tests/TerraScale.Dashboard.E2E`; the solution contains the one production
application and this test project. TUnit owns discovery,
execution, assertions, lifecycle and TRX reporting. Its shared fixture starts
two real Kestrel servers on operating-system-assigned ports with common demo-provider
state. Existing browser journeys run as E2E children of TUnit while native
HTTP and MCP assertions use TUnit directly. The native and JavaScript browser
harnesses use Playwright 1.63.0 and its matching Chromium, independently of
newer browsers installed in the cache. The E2E lint guard rejects divergent
harness versions and other test runners. Dependencies are pinned and locked;
see [TUnit installation](https://tunit.dev/docs/getting-started/installation/)
and [running tests](https://tunit.dev/docs/getting-started/running-your-tests/).

All dashboard test targets run TUnit E2E journeys. `test-fast` runs the
non-browser journeys, `test-slow` the browser journeys and internal-billing
policy suites (`[Category("Slow")]`), and `test-e2e` both. There is no
unit-test target. Hot reload is an explicit TUnit E2E journey that needs a
tailnet dev machine; it runs only through `make test-hot-reload`.
The journeys exercise desktop/mobile rendering, accessibility, native
forms, document updates, schema/SQL, credential issuance, MCP visibility and
revocation, and organization checkout. The hot-reload check temporarily changes
C# copy and CSS, checks the open Tailscale browser, then restores both files and
checks the original appearance.

`perf` is an explicit TUnit browser E2E journey. It records one hundred
actual cold/warm samples across login, overview, databases, SQL and usage,
using five runs on desktop and a throttled mobile browser. It writes measured
values and p75 summaries under `test-results/dotnet/performance.json`;
these measurements are not a live deployment benchmark. The
`check-e2e-shards` target lists the single TUnit inventory.
The source guard checks repository-wide C# test projects and attributed tests.
It is a lint boundary, not proof that a test exercises the whole application.
The latest explicit performance journey passed in 1 minute 54 seconds with
all one hundred measured samples, with no skipped cases.

`test-live-readiness` explicitly launches the previously built production
program without a demo host, identity injection or provider transport override.
It requires real startup and HTTP `/readyz` success, with bounded startup,
response streaming and process cleanup. Child logs and provider bodies are
discarded rather than copied into reports. This is a positive qualification:
missing services fail; they are neither skipped nor accepted as success. Normal
startup performs the fixed database/collection bootstrap using the automatically
delivered state identity. It is not a read-only production probe. The actual
TUnit run failed before a listening host became ready (one failed case, zero
skips, 906 milliseconds). The descriptor was separately observed absent and
service DNS still returned NXDOMAIN. This qualifies no live state operation,
auth operation, Stripe delivery or account-to-TypeScript-SDK flow.

(Historical: this journey and its Go reference capture were removed on
2026-10-12.) The strict visual comparison exported 110 screenshots and DOM
snapshots from the actual Go test application, then compared C# status, structure, forms,
links, styles and every pixel. It does not ignore unported routes or loosen
pixel tolerance. Font/image loading and animation capture are synchronized.
The capture exports actual authenticated workspace identities, scoped invitations
and database/backup observations through native MCP. The C# test host imports
these observations without importing grants or inventing timestamps. Both
harnesses require two consecutive identical screenshots from their own host;
comparison still checks every pixel and all captured form constraints. This
uses the capture-stability technique documented by
[Playwright](https://playwright.dev/docs/test-snapshots#generating-screenshots).
Both harnesses now also wait for actual editor upgrades, including editors in
hidden dialogs that add form controls. Changing the capture algorithm requires
an explicit reference refresh with matching source, algorithm and snapshot
digests; ordinary comparison never regenerates its reference.

An earlier complete ordinary TUnit run on 2026-10-12 passed 247 of 248 cases
(99.6%) in 11 minutes 55 seconds, with no skips. All functional cases passed,
including thirty internal-billing policy cases, twenty-two CLI cases, the
native wrong-user ownership-confirmation refusal and delivered-month cap-change
retry. The full run also passed browser journeys, C# and CSS hot reload,
seventeen process/TLS-HTTP Stripe audit cases, database/backup/credential
journeys, all thirteen state-bootstrap cases and the unchanged TypeScript SDK
journey against the test producer. Strict screenshot parity remains its sole
failure: 57 of 110 states (51.8%), including a recurring fifty-pixel desktop
login raster difference. The fixed raster arguments did not establish stable
exact rendering. No tolerance was relaxed and the failed gate remains open.
These are discovered demo E2E pass rates, not migration completion percentages.

Ten Luna agents subsequently reviewed separate feature and test areas. Root
review confirmed missing positive browser coverage for seeded notifications,
the mobile navigation drawer and project map controls. New native TUnit journeys
passed all six focused cases with no skips: two notification viewports, two
project-map viewports, one mobile-drawer case and one simultaneous-host case.
Notification mutations use actual native forms and Datastar patches, then read
the persisted result through the other replica's HTTP/MCP boundaries. Project
map selections submit the canonical native controls and persist across replicas.
The host fixture now binds free operating-system-assigned ports; four live
listeners served readiness, login and anonymous identity requests concurrently.
The expanded ordinary TUnit run then completed 254 cases in 11 minutes
40 seconds: 251 passed, three failed and none were skipped
(`test-results/tunit/post-luna-full-e2e.trx`). All six added cases passed in the
complete run. Strict comparison passed 58 of 110 states and remained one
failed test. Both native nominee enrollment cases failed before reaching their
ownership/report boundary: the setup froze its clock at noon, while the HTTP
client evaluated the resulting signup-cookie expiry against actual wall time.
The billing journeys now use current UTC time and an independently captured
expected month. Their focused TUnit run passed all thirty cases, including both
nominee boundaries and the dynamically selected UTC month boundary, with no
skips in 5 minutes 57 seconds (`test-results/tunit/utc-clock-billing-e2e.trx`).
Failure diagnostics retain response status and sanitized producer counts;
successful enrollment additionally requires exactly one pending-token redemption.
The subsequent complete ordinary TUnit run confirmed the corrected setup:
253 of 254 cases passed (99.6%), none were skipped, and the run took 12 minutes
9 seconds (`test-results/tunit/utc-clock-full-e2e.trx`). Both nominee boundaries
passed their complete signup, invitation, ownership change, report-race and
current-owner inbox flows. Strict visual parity was the sole failed case,
passing 58 of 110 comparisons (52.7%). Eighteen Luna agents contributed focused
reviews across these checkpoints. These are test-host results, not migration
completion or live qualification percentages.

After that run, inspection localized the fifty differing login pixels to the
SFO map marker's animated ring. Later captures in the same C# context matched
the original Go bytes. Both harnesses now await the pause's
[ready promise](https://developer.mozilla.org/en-US/docs/Web/API/Animation/ready)
and commit frames after the final freeze. Snapshots additionally compare map
opacity, transform, dash offset and actual animation time/pending/play state.
The explicit original-Go refresh at 07:29:34 UTC passed, with source/capture
and snapshot provenance intact. Two subsequent comparisons against that same
reference each passed 58 of 110 states (52.7%): desktop/mobile login had zero
different pixels, all map states matched and no pixel-only failure remained.
The three native reference journeys passed in the first four-case comparison
run. Strict parity still fails on the other fifty-two feature/copy differences;
two successful login captures are not proof of permanent capture stability.

An earlier complete ordinary TUnit run on 2026-10-12 passed 158 of 159 cases
(99.4%) in 4 minutes 32 seconds, with no skipped cases. Every functional case
passed; strict visual comparison was its sole failure, passing 57 of 110 exact
states (51.8%). These percentages describe discovered demo E2E cases and
rendering comparisons, not overall migration completion or live qualification.
That run includes repaired provider auth, native/browser billing review,
membership and recovery, all sixteen platform-role cases, credential/data SDK
operations, independent storage measurement, reporting, backup-policy and
legacy settings aliases. C# and CSS hot reload reached the open Tailscale
browser and restored the original files.

A later thirty-case focused run passed nine public-command/native-device cases,
seven branch-recovery cases and fourteen readiness/recovery cases. The next
compiled checkpoint ran one hundred cases in 4 minutes 3 seconds: ninety-six
passed and four failed, with no skipped cases. All nineteen internal-billing
command and policy E2Es passed, as did all eleven expanded branch cases, sixteen
readiness cases, thirteen independent state/SDK bootstrap cases and the native
reference journeys. The remaining functional failures were three backup-cron
expectations: genuine Cronos 0.13.0 Standard accepts macros such as `@daily`,
and the production consumer now validates the same parser contract locally.
The incorrect expectations were repaired and all backup cases passed in the
following compiled checkpoint.

The follow-up compiled checkpoint passed all 129 functional cases, with strict
visual parity its sole failure (130 total, no skips, 6 minutes 19 seconds).
This includes all twenty-one internal-billing policy/race/browser-MCP cases,
nine command-authorization cases, nine regional-scope refusal/recovery cases,
all four customer/credential SDK cases and every backup correction. The strict
comparison passed 56 of 110 states (50.9%). The hidden document-editor loading
race is repaired; its desktop and mobile form snapshots now match. Tiny
raster differences moved to the desktop login and code-entry views (forty-two
and eighty-three pixels), while the earlier rejected-login/code-prefill cases
matched. This remains a failing gate rather than proof of exact rendering.

The next compiled checkpoint passed all 100 functional cases (101 total,
no skips, 6 minutes 37 seconds), including twenty-three policy cases, sixteen
public command/authorization/argv cases and the native identity, role, billing
MCP and bootstrap scopes. The final forced-refresh cap-change regression and
wrong document-key refusal passed. Strict visual comparison passed 58 of 110
states (52.7%); every earlier tiny raster discrepancy was absent in this run.
Both hosts used the same fixed baseline Skia CPU path and full rasterization,
as described in
[the official Chrome tooling switch documentation](https://github.com/GoogleChrome/chrome-launcher/blob/main/docs/chrome-flags-for-tools.md#rendering--gpu).
The explicit capture at 2026-10-12 06:28:14 UTC passed with matching source,
capture/comparison and snapshot digests. The E2E lint guard also verifies
identical raster arguments in both harnesses. Pixel tolerance remains zero;
ordinary browser journeys retain their standard launch. The next full run
must confirm stability and the latest source refinements.

The following focused checkpoint passed all 109 cases with no skips in
7 minutes 3 seconds: twenty-nine internal-billing policy cases, twenty-two
public CLI authorization/argv cases, all twenty-four auth-provider cases,
thirteen private identity cases, sixteen platform-role cases, four organization
MCP cases and the invitation SDK journey. Native second-owner signup,
invitation/transfer and current-owner alert delivery passed, as did exact
metering/pricing failure fields, currency, historical payment-validation
preservation and valid/invalid base-zero numeric arguments. These are test-host
results. Review then found a deduplicated alert retry that returned before the
last owner/cap check and a provider-denied ownership confirmation translated
into generic unavailability. The fixes and strengthened native regressions
passed the latest complete 248-case run above.

Before the billing ownership refactor, ten focused accessibility and auth
regressions passed: mobile drawer Tab/Shift+Tab trapping; nested notification
Escape, Close and backdrop focus restoration; primary/backup region keyboard
selection; and lowercase plus mixed-case/whitespace signup canonicalization.
The complete `focus-regressions-full-e2e.trx` run passed 257 of 258 cases with
no skips in 12 minutes 12.019 seconds; its single failure was the known strict
visual comparison. Build completed with zero warnings and errors, and lint
found 47 journey classes. Strict comparison remains 58/110 (38 unbound-auth
503 states and 14 producer/copy feature states); the baseline was not relaxed.

(Historical, before the visual journey was removed.) Fifty-two strict visual
differences involved unsupported producer operations and original demo-only
copy/samples; they remained visible failures. The settled
captures above eliminated the small login raster discrepancy in their observed
comparisons. Final
organization and owner-inbox writes are separate conditional writes: a change
after the last canonical organization read cannot be atomically excluded by
the owner-account CAS alone. Refresh-window refusal coverage does not qualify
a cross-document transaction or deployed ownership consistency.

The Stripe audit implementation now uses the official Stripe.net SDK and the
fixed production identity descriptor. Its process/TLS-HTTP E2E coverage is in
`StripeAuditJourneyTests`; all nineteen cases passed in the final focused
run with no skips in 8.043 seconds (`stripe-direct-audit-final-e2e.trx`).
Payment operations use `StripeClient.V1` directly, onboarding injects
`StripeBilling`, and billing plan models plus internal reporting live under
`Features/Billing`. The internal report schema and noncharging behavior are
preserved. The combined twenty-case audit/webhook run also passed with no
skips (`stripe-direct-final-focus-e2e.trx`), including both original webhook
URLs, signatures, stale events, key-mode refusal, retryable provider outage,
the exact 1 MiB body ceiling and chunked overflow. The subsequent complete
ordinary run passed 260 of 261 TUnit E2E cases (99.6%) with no skips in
12 minutes 39.086 seconds (`stripe-direct-full-e2e.trx`). All eighty billing,
Stripe-audit and internal-billing CLI cases passed. Build completed with zero
warnings/errors and lint passed with 48 journey classes. C# and CSS hot reload
passed; source and bundled CSS hashes matched their pre-run values afterward.
Fresh desktop/mobile billing captures were inspected. Strict visual parity
remains the sole ordinary-suite failure at 58/110 exact comparisons (52.7%);
the same fifty-two states fail as before the refactor, with no newly failing
states and no relaxed reference. The explicit performance E2E passed again
with all one hundred samples in 1 minute 54.207 seconds
(`stripe-direct-performance-e2e.trx`), without establishing a live latency
budget or live billing qualification. The audit creates no charges. There is no
separate unit-test runner; screenshots and reports are ignored under
`test-results/`, never committed.

A passing test-only demo is evidence about dashboard behavior. It does not
prove real Stripe delivery, live ts-auth/TerraBase authorization, physical
replication, live SDK interoperability or persistence across deployed restarts.

The native TUnit account-to-customer-SDK journey verified against the
test producer: signup, organization/project/database creation, native API-key
issuance and eight point-document operations with the unchanged official
`TerraScale.DotNet` SDK, in process (formerly `@terrascale/sdk` 0.0.3 under
node; both send the same wire requests). The test transport changes the TCP destination while
preserving the SDK's request host, path, authorization and body. The three
official .NET SDK credential/scope journeys previously passed with the unmodified
`130925898daf0207134d9582b7b672a5a1c3233b` candidate. These protocol results do
not establish admission or live-service qualification of that SDK revision.
The latest placement validation regression was repaired and all four SDK
journeys passed again in the focused follow-up.

## Runtime integration gates

### Current blockers and direction (2026-10-12, 19:11 UTC, read-only)

- **Discovery.** `atlas.terrascale.tech` is NXDOMAIN and no producer
  serves `/v1/discovery`. The dashboard no longer calls it: TerraBase is
  `https://api.terrascale.tech/`, and the region list is the answering
  region from `/healthz` (see `docs/identity-descriptor.md`, "TerraBase
  origin and regions"). Requests to a database's region use its regional
  host; the dashboard's direct HTTP calls add ts-dotnet 0.0.2's
  `X-TerraScale-Region-Pin` and typed 421 handling, which the vendored
  0.1.0-dashboard package predates. A TerraBase region catalog remains an upstream dependency.
- **ts-auth.** The deployed `0.1.0-alpha+55979ea3` answers `/readyz` 503 and
  `/.well-known/jwks.json` 500. Its current source requires an operator to
  provision the first signing key. `auth.terrascale.tech` is NXDOMAIN.
  The dashboard calls ts-auth only at its global issuer
  `https://auth.terrascale.tech/terrascale`, as ts-auth serves it from every
  region; it no longer uses Atlas auth discovery or regional auth hosts.
- **Identity delivery.** The retired deployment `ts-dashboard` has no secrets. The app
  reads four runtime secrets from its environment (ts-auth client secret, session
  encryption key, Stripe secret and webhook keys) and keeps its state in the
  seeded `platform-dashboard` database through TerraBase's private listener,
  with no state key. Names, formats and owners are in
  [identity-descriptor.md](identity-descriptor.md).

`c379cc3b2cf724d2c10c753397479b5cf2b4f44d` and SDK main
`bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. Producer CDC R3 has since
landed as an internal replication tailer. It is not a public document-history
or query-subscription delivery API; earlier historical checkpoints below
describe the older revisions they inspected.

The dashboard now obtains narrowly scoped `databases:read` project tokens
through the producer's RFC 8693 exchange for canonical analytics, replication
and schema routes. It checks the signed token's issuer, audience, subject,
client, organization, project, scope and bounded lifetime before use. Legacy
management database/backup routes retain their producer-supported sign-in
authorization. The pinned SDK lacks the exchange helper, so the adapter uses
the actual served OAuth contract and the SDK's discovery/JWKS boundary.

Analytics and replication consume the current unpaged, nested reporting
contracts and aligned UTC windows. Analytics filters map dashboard region
codes to Atlas IDs. Missing storage remains unknown, replication sent and
received remain separate, and the homepage shares these adapters. Schema
reads use project authority through the official Documents SDK. New native
TUnit journeys cover signed exchanges, invalid/refused grants, recovery,
global issuer admission, canonical reporting paths and schema authority.

The final regression review also corrected the database-detail replication
empty-state message and the test producer's canonical replication dispatch.
Webhook bodies still have a 1 MiB acceptance limit; a fixed 64 KiB rejection
drain allows slightly oversized chunked uploads to receive 413 without
buffering or processing rejected bytes. The 27 focused backup, readiness and
webhook E2Es pass, including exact-boundary chunked requests on both aliases.

A final presence-only check at 17:17:17 UTC still found no delivered identity
descriptor and no Atlas DNS resolution (public DNS status 3, zero answers).
This merge establishes no live startup, persistence or payment qualification.
The final explicit genuine-production readiness E2E also failed its positive
startup assertion: zero passed, one failed and no skips in 896 milliseconds
(`final-merge-live-readiness.trx`). Its failure is retained separately from
test-provider functional coverage and is not treated as a successful check.

Read-only qualification probes at 2026-10-12 05:36–05:39, 07:01 and 07:39 UTC could not resolve
the automatically discovered Atlas and GRU auth/API hosts. Independent public
DNS also returned NXDOMAIN. Live discovery, readiness, OpenAPI and account-to-SDK
qualification therefore remain unverified; inaccessible endpoints do not prove
which features a deployment serves. Producer main advanced to `740ad0d6`;
dashboard-used auth and data wire routes/DTOs remain unchanged from `b76c4621`.
Its new [signing-key contract](https://github.com/TerraScale/terrascale/blob/76f5d5ce20936f09b484de3084af2c21deb84ea2/docs/auth/signing-keys.md)
returns `503/no_signing_keys` without keys and requires a separate operator
provisioning process. Automatic first-party provisioning remains an owning
producer/secret-delivery qualification gate. The current engine also advances its
storage format to version 3; actual deployed migration, CAS, replication and
backup durability remain unqualified. Public NuGet's
`TerraScale.DotNet` index returned `404`; candidate digest verification is
separate from durable package publication and producer admission.

A fresh 12:50 UTC check still found Atlas NXDOMAIN through both the system
resolver and Cloudflare public DNS. Without discovery, this pass did not infer
current regional hostnames or claim regional HTTP results. The fixed identity
descriptor was absent in a presence-only check. Existing configured upstream
remotes established producer main `c691f94716c5d6adb41146baa6436304d1d06c88`
and SDK main `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. The producer's two
commits after `740ad0d6` change only CI deployment/preview conditions; its source
and dashboard-facing contracts did not change. This establishes no deployed
service readiness, package admission or automatic identity delivery.

The 14:43 UTC presence-only discovery check remained unsuccessful: the system
Atlas lookup failed, Cloudflare DNS returned NXDOMAIN with zero answers, and
the fixed identity descriptor was still absent. The observations are recorded
in `test-results/dotnet/current-service-discovery.json`; they qualify no live
application or state operation.

At 15:28:44 UTC, a new presence-only check again found the fixed identity
descriptor absent, Atlas `getent` failed, and public DNS returned NXDOMAIN with
zero answers. Producer `origin/main` was
`014c9928931ff357873b7935b6dda925bd65390d`; the SDK remained
`bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. The new GitHub Actions grant is
pinned to an operator workflow and does not establish dashboard customer-auth
binding or automatic dashboard runtime identity delivery. CDC R3 remains
pending. This checkpoint establishes no live or deployed qualification, and
does not verify the current billing refactor.

At 16:23:13 UTC, the fixed identity descriptor was still absent, Atlas failed
system resolution and public DNS returned NXDOMAIN with zero answers. The
public NuGet package index returned HTTP 404; this does not establish whether
a private package source has a release. The presence-only receipt is in
`test-results/dotnet/current-service-discovery.json`; no secret material was read.

The previous explicit production-readiness TUnit E2E also failed: zero passed,
one failed and none were skipped in 870 milliseconds
(`test-results/tunit/final-live-production-readiness.trx`). The genuine
production entry exited before listening. Its positive readiness assertion
remains failed; the demo suite does not qualify automatic identity delivery,
live account-to-SDK access, or deployed persistence migration.

After the billing refactor, the new explicit production-readiness E2E remained
failed: zero passed, one failed and no skips in 835 milliseconds
(`stripe-direct-live-readiness-e2e.trx`). The actual production executable again
exited before listening, without a demo host or identity injection. This remains
a failed positive qualification, separate from the 260 passing ordinary demo
cases and successful performance measurement. The wider migration goal remains
incomplete until strict appearance parity and real identity, authorization,
state, SDK and Stripe delivery gates are satisfied.

The immutable `740ad0d6` producer source also needs live regional qualification.
[Management endpoints forward to the catalog primary](https://github.com/TerraScale/terrascale/blob/740ad0d6b2d0c409e2606ee187b6c77892803e8e/src/TerraBase/Features/Replication/CatalogForwarder.cs#L39),
while [managed database creation checks the physical host region](https://github.com/TerraScale/terrascale/blob/740ad0d6b2d0c409e2606ee187b6c77892803e8e/src/TerraBase/Features/Management/CreateManagedDatabaseEndpoint.cs#L48)
and projects a forwarded answering region in its response. A real follower
create must therefore be qualified against the deployed producer. Its
[replication registration explicitly uses `NoChangeGroupSource`](https://github.com/TerraScale/terrascale/blob/740ad0d6b2d0c409e2606ee187b6c77892803e8e/src/TerraBase/Features/Replication/ReplicationExtensions.cs#L21)
pending the CDC tailer; replica metadata alone establishes no data convergence.
The dashboard now validates existing canonical regional scope using ordinary
authenticated SDK reads. It no longer attempts an operator-only chosen-ID
project commit when scope is missing. Nine new native HTTP/MCP recovery E2Es,
including the unchanged customer SDK after genuine catalog recovery, passed
the 129-functional-case compiled checkpoint. These source observations do not establish what an
inaccessible deployment currently serves.

At that checkpoint, the C# consumer expected machine identity at
`/run/secrets/terrascale/dashboard.json`. The file was absent in the
07:39 UTC presence-only check; no secret material was read. This historical
consumer boundary has since been replaced: production runtime secrets come from
OCI Vault via ts-orch using instance principal, delivered as secret files. CI secrets are
GitHub Actions secrets; the complete inventory lives in terrascale under
`ops/secrets/`. See [`identity-descriptor.md`](identity-descriptor.md).
Provisioning and rotation still require live qualification.
There is no endpoint override, production demo selection or manual fallback.

`DashboardCredentials` describes state-project/service credentials, ts-auth
client and management credentials, session encryption and vendor-issued Stripe
keys. The first-party state database is `dashboard`. It stores accounts,
organizations, sessions, credentials and indexes, auth flows/budgets/device
grants, invitations, notifications, saved queries, PAT indexes and billing
bindings. Hosted startup now establishes the fixed database and twenty
collections through the official SDK after receiving an admitted state-project
identity. Automatic tenant/project authority, OCI Vault delivery, live bootstrap
qualification and import of existing Go persistent state are still required.

Endpoints use TerraBase's global origin `https://api.terrascale.tech/`
without region switches; Atlas is not called.

Initial live validation could not resolve `atlas.terrascale.tech`; the pinned
TerraBase release URL in the SDK testing helper returned HTTP 404, and no
matching release binary was cached locally. Live service authentication,
persistent restart tests and SDK interoperability remain unverified. No live
test is reported as passing.

## Remaining parity and qualification

The removed Go source and browser journeys (git history up to `447056dd`)
were the authoritative inventory. Outstanding work includes platform-admin
claims and the Go `admin_*` MCP tools, complete Go MCP tools/resources,
subscription-change qualification and any remaining producer-backed lifecycle
behavior. Analytics, usage, spending controls, settings, invoices and denied
admin routes are implemented; their test-host E2E journeys are separate from
live producer qualification.
Backups, imports, document history and customer-auth provisioning require real
producer contracts; the removed Go paths also contained unavailable operations.
The SDK does not support viewer membership or invitee-side upstream decline.
Nonempty project/organization deletion needs real orchestration rather than
a simulated deletion worker.
The Platform REST API (`https://platform.terrascale.tech`, `GET /openapi.json`,
described in [platform-api.md](platform-api.md)) is not ported. The C#
application has no host routing for it, no `/v1/organizations/...` routes and
no OpenAPI document.

### Go-only interfaces: compatibility disposition (2026-10-12)

A consumer search cloned the default branches of twelve TerraScale repositories
(terrascale `4cfbf85d`, ts-status `d1d2ce8c`, ts-go `9637a299`, ts-js
`8c8ded2c`, ts-dotnet `2d23b1be`, ts-docs `61eb6dfb`, ts-landing `bb3e6ccd`,
ts-cos `20674fe9`, ku `a0f4b8a6`, ts-reviewer `e7e74ab7`, ts-resources
`f3b54ea3`, ts-rust `8a5d9836`) and grepped them for each interface. "No
consumer" means no code, CI or test call was found; it is not proof that no
external client exists.

| Interface | Consumers found | Disposition |
| --- | --- | --- |
| `GET /health/ready`, `GET /health/live` | ts-status reads the commit after `+` in `version`; its public probe target must use the canonical `https://dash.terrascale.tech/health/ready` | Ported for GET. Both return `{"status":"ok","version":"<version>+<commit>"}`; ready runs the same check as `/readyz`, and a failure is a 503 HTML page rather than Go's JSON `unavailable` body. HEAD and the `backend` field are not ported. The image build requires the commit as `--build-arg SOURCE_REVISION`. |
| `GET /api/v1/status/live`, `/api/v1/status/ready` | none | Not ported. |
| `GET /access` (redirect to members) | docs only (terrascale `docs/terrabase/management-api.md:862`) | Not ported. |
| Platform REST API | docs only (terrascale `docs/auth/platform-contract.md:313`) | Not ported. Whether the contract is still required is a decision for its owner (ts-auth platform contract). |
| MCP `admin_*` tools | none | Not ported. They need producer administration APIs that do not exist yet (see the administrator audit below). |
| Sentry/GlitchTip errors and CSP `report-uri` | not an interface; the Go app read `SENTRY_DSN` and `DASHBOARD_CSP_REPORT_URL` | Not ported. The C# app sends no error reports. |

Before deployment, finish native browser journeys, official SDK distribution
and live secret-delivery/ts-auth/TerraBase checks, auth-budget contention qualification,
persistent restart/cross-instance checks, and the whole
account-creation-to-ts-typescript-SDK flow. The image (`Dockerfile`) and
ts-orch readiness check (`/readyz`) target the C# application, and the
Go runtime/tooling was removed. This repository has no CI or release
workflow yet. Keep `AGENTS.md` and `CLAUDE.md` untouched; the user's C# request supersedes
their older Go stack instructions.

## Identity, administrator and TypeScript qualification audit

The delivery observations below are historical. The current runtime reads
identity from OCI Vault secret files delivered by ts-orch; see
[`identity-descriptor.md`](identity-descriptor.md). CI secrets are GitHub Actions
secrets. The complete inventory lives in terrascale under `ops/secrets/`.

The read-only audit on 2026-10-11 found no delivered
`/run/secrets/terrascale/dashboard.json` on this development machine.
`DashboardStateInitializer` now establishes the fixed `dashboard` database
and the twenty collections in `DashboardStateSchema` before traffic
admission. It uses the official SDK's idempotent database PUT, creates only
genuinely absent collections, and rereads concurrent `name_taken` conflicts.
Readiness verifies every required collection without writes. Unrelated
conflicts, mismatched identities, authorization failures and outages fail
closed. `ManagementCredential` is validated but provides no machine-bootstrap
authority. Service provisioning automation must create the canonical service project and
admit its storage credential, then deliver and rotate the complete descriptor.
Collection inventory includes public-account identity claims, pending billing
changes and original-scope checkout bindings. A delivered file alone does not qualify TLS,
issuer/client bindings, project ownership, collection permissions, durability,
rotation or a persistent restart.

The current [producer OIDC source](https://github.com/TerraScale/terrascale/blob/aa8f3474523110ab6bacedf3f5355a791fcb72c4/src/TerraScale.Authentication/Features/Oidc/OidcEndpoints.cs)
supersedes the older TS-598 proposal: platform authority is
`platform_role: "admin"` on a signed first-party access token only. It is
reread from the private user record at sign-in and refresh and is absent from
ID tokens and UserInfo. UserInfo exposes only subject, email and verification,
and checks current user epoch, grant activity and MFA eligibility. The producer
does not serve token introspection. The dashboard therefore checks session
activity through the official SDK's UserInfo call, binding the exact subject
and verified email; only provider `401` means inactive, while outages remain
dependency failures. Twenty-four native provider E2E cases passed this contract,
including missing password-mutation/revocation handlers and a lost mutation
response, which must never become fabricated password-change success.

The C# administrator authorization changes now force the existing persistent
refresh lease before validating ES256, `at+jwt`, issuer, audience, subject,
verified email and the first-party tenant. Concurrent requests share a new
rotation; unchanged or missing refresh credentials cannot authorize an operator.
All sixteen role E2E cases passed in the latest full run. Ordinary principals receive
`403`; verified operators receive an explicit `503` for unavailable original
administration APIs. Local fields, organization roles, ID-token claims,
management keys and email allowlists cannot grant platform access.

The newer [official SDK explorer](https://github.com/TerraScale/terrascale/blob/bcd20603f11f3aa54dc54cec6e410b4fe9e24151/src/TerraScale.DotNet/TerraBase/Platform/TerraBasePlatformClient.cs)
requires a per-request access-token provider and offers read-only exploration
of first-party databases, collections and documents. It is not part of the
currently pinned SDK candidate and does not supply the original cross-account
inventory, user/organization/project/plan mutations or public operator grants.
Those interfaces and their live qualification remain required before original
administration can be considered implemented. The private user-record field
does not constitute a public atomic grant/revoke API or a last-admin invariant.
The current producer also lacks the SDK-declared signed-in password PATCH and
revoke-all handlers. Its headless password-reset flow is served. Passing protocol
fixtures for signed-in password changes do not establish that missing live flow.

The [official npm registry](https://registry.npmjs.org/@terrascale%2Fsdk)
reported latest `@terrascale/sdk` version `0.0.3`; `0.3.0` was not published.
The inspected [immutable official package](https://registry.npmjs.org/@terrascale/sdk/-/sdk-0.0.3.tgz)
provides project-scoped Documents CRUD/query handles but accepts only compact
access JWTs or legacy `tb_` keys, rejecting raw `tsk_`/`tbk1` credentials.
Its README says JWT producer compatibility remains unqualified and its
Management/Admin entrypoints make no HTTP calls. Its hosted
`oidc.token(clientId, clientSecret, request)` client-credentials API requires
an explicit `resource` and `scope`, without a `project` argument. This differs
from [TS-598 platform API-key exchange](https://github.com/TerraScale/terrascale/blob/a095400a19bcc432ac27d0afaca4eba042b636a7/docs/auth/platform-contract.md#61-api-key--token-client-credentials),
which binds the key's project and accepts optional `project`/`scope`.
Those grants cannot be treated as interchangeable without producer evidence.

The required real whole-flow lane must enroll a disposable account and verify
its email through ts-auth, complete onboarding with real persistent provider
organization/project IDs, create a database and scoped credential through
native dashboard forms, obtain a provider-admitted project access token, then
use the official TypeScript package to write/read/query/delete data and verify
the same data in the dashboard. Cross-account denial, credential revocation,
issuer/audience and project binding, and persistence across a cold dashboard
restart are qualification requirements. The removed Go `sdk_test.go` covered
ts-go; the passing C# demo browser journey uses test-only providers. Neither
establishes this live TypeScript lane, which remains unverified.

## State bootstrap and preservation design

Fixed database/collection startup is implemented and all thirteen native TUnit
E2E scenarios pass against an isolated external producer protocol fixture.
Service-project provisioning, OCI Vault delivery and
the Go-state import remain incomplete. No production data has been read,
changed or deleted by this audit.
The [TS-598 introduction](https://github.com/TerraScale/terrascale/blob/a095400a19bcc432ac27d0afaca4eba042b636a7/docs/auth/platform-contract.md)
specifies a TS-602 greenfield identity bootstrap with no migration of old
organizations, memberships, invitations or legacy `tb_` keys. That producer
decision does not itself authorize this dashboard migration to delete existing
data. Preserving dashboard-owned preferences, history and Stripe bindings is a
separate cutover decision. The Go database must remain intact until that scope,
the account/resource mapping and a restore procedure are reviewed.

The pinned [official Documents client](https://github.com/TerraScale/terrascale/blob/130925898daf0207134d9582b7b672a5a1c3233b/src/TerraScale.DotNet/TerraBase/Documents/TerraBaseDocumentsClient.cs)
already exposes the necessary database operations:

| Operation | Existing SDK contract | Bootstrap use |
| --- | --- | --- |
| Create the fixed state database | `TerraBaseDatabase.CreateAsync` performs idempotent `PUT` | Call after a delivered, authorized state-project identity exists; verify the returned project and name. |
| Establish collections | `GetCollectionAsync` and `CreateCollectionAsync` | Read each fixed collection; create only when the producer reports it absent. On a concurrent `name_taken`, reread and verify it. Authentication, discovery, other conflicts and outages fail readiness. |
| Observe readiness | `GetCollectionAsync` | Verify all twenty required collections without writing. |
| Write migration records | `PutDocumentAsync` with `TerraBasePrecondition.Absent` or `.Matches(version)` | Preserve create-only semantics and opaque producer versions; never replace an unrelated destination record. |
| Commit related records | `TransactAsync` with `Create`, conditional `Put`, `Assert` and `AssertAbsent` | Atomically write bounded related records and their durable migration checkpoint. The SDK admits 1–1024 operations in one database, not a cross-database transaction. |

Service provisioning automation must first establish the dashboard service's canonical
organization/project and admitted storage credential, then automatically
deliver the descriptor. The [official Management client](https://github.com/TerraScale/terrascale/blob/130925898daf0207134d9582b7b672a5a1c3233b/src/TerraScale.DotNet/TerraBase/Management/TerraBaseManagementClient.cs)
can create organizations, projects and databases for an authenticated ts-auth
user or a producer-trusted private-network caller. This SDK revision exposes
no service-credential bootstrap grant, and a public request's `userId` must
come from an authenticated principal. It is not a documented
machine-bootstrap grant. A guessed service user, a browser-supplied user ID or
the unused `ManagementCredential` cannot establish that authority. The
dashboard must not reuse ts-auth's separately owned storage-signing key.
Credential acquisition and rotation need the owning producer and OCI Vault delivery
contract; the available SDK operations do not close that prerequisite.

Database/collection startup runs automatically before traffic admission, using
the fixed inventory without feature switches or manual credentials. Replicas
tolerate concurrent creation through documented idempotence/conflict
contracts. A cold restart must preserve existing data. Qualify empty
bootstrap, interrupted creation, simultaneous replicas, denied scopes,
malformed identity, discovery/secret-delivery outage and credential rotation against
real services before production cutover.

The Go state format needs a versioned adapter rather than collection copying.
In the removed Go source (git history up to `447056dd`),
`internal/adapters/terrabase/schema.go` provisions nineteen collections;
`records.go` stores one domain record inside a `kind: record` envelope and
separate uniqueness claims. Its document key carries identity, while references
and revision/auth-version fields remain in the envelope. `aggregate.go`
reconstructs account/organization children and rechecks root versions before
accepting a snapshot. The C# account key is instead SHA-256 of the ts-auth
subject, and its organization projection contains children. Shared collection
names such as `sessions`, `device_grants` and `login_throttle` also have different
schemas. Import must target an isolated destination database/project; pointing
the new consumer at the Go state database does not migrate it.

A preservation job must first produce a read-only inventory with source
versions, record counts, ownership/reference checks and rejected records.
Account mapping requires a provider-verified subject and issuer; email alone
is insufficient. The Go `MirrorOrganizations` helper assigns Business and
owner membership to newly mirrored organizations by local account email, so
it cannot be reused to grant permissions. Provider organizations, memberships,
projects and databases must be reconciled through the real owning APIs using
their canonical IDs. If the fresh producer uses new IDs, an explicit verified
mapping is required; UUID and ULID strings cannot be converted by assumption.

| Source state | Preservation rule |
| --- | --- |
| Name, phone, locale, notification/read state and saved queries | Preserve only for a mapped subject and verified referenced resources; validate current field bounds and retain provenance. |
| Organization/project/database metadata | Rebuild from authoritative provider records and verified mappings; cached owner/member/status fields confer no authority. |
| Stripe customer, subscription and checkout bindings | Reconcile current Stripe objects and verified ownership before writing the C# bindings; email matching and historical paid flags are insufficient. |
| Existing plan catalog | Inventory intentional edits and deletions. Go's catalog marker makes existing plans authoritative; reseeding defaults must not silently restore deleted plans or alter subscriptions. |
| Browser sessions, refresh grants, PATs, CLI grants, invitation secrets, API keys and local administrator/password fields | Do not reinterpret them as new grants. Require fresh provider authorization and reissue/revoke through the owner; secrets are excluded from the inventory/report. |

Import batches must remain within producer operation/body limits and carry a
persistent versioned checkpoint binding the source snapshot, destination,
mapping and batch digest. Create-only writes plus conditional checkpoint
updates must make retry after lost delivery safe; unexpected existing data or
changed source versions stop the job. A paginated scan alone is not a coherent
snapshot: use an admitted snapshot/branch contract or a coordinated cutover
with source-version validation. No atomicity across the source and destination
databases is claimed. Keep the original state and a verified restore path until
fresh sign-in, provider permissions, Stripe reconciliation, restart durability
and the whole TypeScript SDK lane pass on the preserved destination.
