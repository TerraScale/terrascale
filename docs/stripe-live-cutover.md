# Stripe live cutover — TS-709

The [zero-configuration contract](zero-configuration.md) requires billing to be
always available. Production runtime secrets are Fly secrets on each Fly app;
CI secrets are GitHub Actions secrets. The complete inventory lives in the
terrascale repository under `ops/secrets/`. This document retains the
authorization, data-integrity and financial acceptance boundaries; it does not introduce a billing enable switch.
Live secret-delivery qualification and removal of current demo/mode gates
remain runtime work. Preparation alone does not authorize charges, refunds or destructive resets.

## Verify the serving target

The production Fly app is `ts-dashboard` (`fly.toml`). The C# application's
fixed public origin is `https://dashboard.terrascale.tech` (`StripeBilling.Origin`);
the webhook is `https://dashboard.terrascale.tech/api/v1/billing/stripe/webhook`.
Verify the app, serving Machine, release, secret names/digests, readiness and
native login immediately before any change:

```sh
fly status -a ts-dashboard
fly releases -a ts-dashboard
fly secrets list -a ts-dashboard
curl -fsS https://dashboard.terrascale.tech/readyz
scripts/smoke-native-login.sh https://dashboard.terrascale.tech
```

On 2026-10-12 at 18:09 UTC the app had no Machines and no releases; the C#
application is not deployed.

Historical (Go application on `https://dash.terrascale.tech`): read-only
inspection on 2026-10-09 at 07:10 UTC found Machine
`d8d96000b74e78` started in `gru` on completed release `v135`, with its check
passing. Readiness, native password login and email-code pages returned `200`;
the login pages retain CSRF controls. Actual authentication was not exercised.
That 2026-10-09 inspection found ten deployed `Payments__Stripe__*` secrets,
but no `STRIPE_*` or `DASHBOARD_PUBLIC_ORIGIN` settings. The Go application ignored those legacy
names, so that inspection found billing unconfigured. Do not add compatibility
aliases; the current Fly-secret contract is documented in
[`identity-descriptor.md`](identity-descriptor.md).

Record Machine/release IDs and Fly secret names/digests in the ticket. Never
record credential values. Reset obsolete application data and
re-bootstrap through the SDK in the existing provisioned database when needed;
do not replace the serving app, cell or volume. Do not touch Forgejo, ts-ops or
homelab hosts.

## Fly secrets and provider verification

The vendor-issued Stripe account key and endpoint-specific signing secret are
stored as Fly secrets on the dashboard app and delivered as environment
variables. CI credentials belong in GitHub Actions secrets. Use the terrascale
`ops/secrets/` inventory; never assemble dotenv files or record secret values.
This documentation does not provision secrets.

Verify account authorization, restricted-key permissions, six distinct recurring
prices, matching live products, the production webhook URL/API version/events
and portal settings. Endpoint signing-secret identity requires verification;
a prefix alone cannot prove a match. Preserve the existing database scope,
native login, CSRF and persistence. Record only object IDs and secret version
identifiers in private operational evidence, never credential values.

The earlier inspection did not identify verified live credential references.
That remains unresolved; do not treat automatic-delivery policy as evidence of
credentials or live financial acceptance.

## Test-to-live gate

This preparation does **not** authorize cutover. Customer snapshots and
organization projections now carry revisions ordered by conditional saves,
independent of replica clocks. A stale projection re-reads Stripe and retries;
exhaustion returns a webhook failure so Stripe can redeliver.
Explicit demo configuration now suppresses Stripe while retaining TerraBase
persistence and keeping simulated billing and security operations unavailable.
Missing billing collections or databases now return retryable webhook failures;
the default persistence HTTP client limits each request to 15 seconds. Real
TerraBase regressions cover clock skew, competing saves and reversed projections.
Verified Stripe sandbox deliveries remain required before live approval.

1. Complete [`stripe-sandbox-dsh207.md`](stripe-sandbox-dsh207.md), including
   signed deliveries, duplicate/out-of-order events, failed-payment recovery,
   checkout returns and invoice reconciliation. Local regression checks:
   `nice -n 19 dotnet run --configuration Release --project tests/TerraScale.Dashboard.E2E -- --treenode-filter '/*/*/(BillingBrowserJourneyTests|BillingPlanJourneyTests|BillingPlanMcpJourneyTests)/*' --report-trx`.
   These TUnit journeys exercise native HTTP/browser and MCP payment flows;
   protocol fixtures do not replace the required actual sandbox deliveries.
2. Inventory dashboard billing through the official ts-dotnet SDK
   (`TerraScale.DotNet`) or public APIs, without publishing
   customer details. Test Stripe customers, subscriptions, invoices, checkout
   sessions and prices cannot be reused in live mode. Check both
   `stripe_customers`/`stripe_organizations` and organization billing projections.
   Unconfigured billing today does not prove older test references are absent.
3. If test bindings or pre-revision Stripe snapshots/projections exist, delete
   obsolete billing data through the ts-dotnet SDK and
   re-bootstrap the application with the current schemas in the existing
   provisioned database. The closed beta permits resetting users, organizations
   and sessions; do not retain old records through migrations, backfills or
   compatibility shims. Confirm the resulting billing bindings, checkout
   records and organization entitlements contain no test references. Dashboard
   entity IDs must be plain ULIDs, and uniqueness must use TerraBase native
   constraints rather than claim documents. These bootstrap prerequisites are
   not yet satisfied on dashboard main `24c44cf3` (Go application, not
   re-checked against the C# state schema), which still uses
   `DocumentIDFor` and `unique_claim`; complete the separate TS-754/TS-755
   rollout before resetting and re-bootstrapping production data.
4. Require the live audit to pass and verify credentials can read/write the
   existing TerraBase billing collections. Require their separate staging evidence before claiming in-place plan
   changes are financially qualified; the runtime mode gate is a removal gap.
5. Ask the user to confirm **activating live billing on `ts-dashboard`** with
   the verified target, configuration versions, runtime hardening, clean data
   and test evidence. Automatic delivery does not authorize charges, refunds or destructive data
   operations; retain the verified-target and acceptance record.

## Production acceptance

Deploy the reviewed runtime through its normal quality and integrity gates.
Fly secrets must supply the verified runtime credentials on the serving app;
demo-switch recipes are retired. Existing delivery and switch paths
remain runtime gaps, so this document cannot certify a completed cutover.

Explicit authorization remains required for actual charges/refunds and approved
obsolete-data resets. Record the resulting release identity and verify readiness,
persistent backend, secure native login and matching provider bindings.

## Verify delivery and reconcile

1. Verify unsigned POSTs to the webhook return `400`. Use Stripe Workbench to
   send an actual signed **live-mode** delivery for a bound live customer and
   verify HTTP `200` and the expected persisted billing state. A Stripe CLI
   test event or an unbound event returning `200` cannot prove live sync.
2. Redeliver the same event and an older event; confirm one subscription,
   unchanged entitlements and no duplicate invoices. Sync fetches current
   Stripe state and uses TerraBase conditional writes, so events converge
   without a second event-ID store.
3. Verify a transient Stripe/TerraBase failure returns `5xx`; a later successful
   redelivery must reconcile billing. Never acknowledge a failed sync.
4. Perform the user-approved real checkout, portal and invoice smoke. Record
   payment/refund results privately; live charges and refunds have real costs.
5. Compare live Stripe state with the dashboard via public APIs. Close TS-709
   only after these checks pass. If no live payment exists, restore the saved
   prior configuration if needed. After any live payment, stop new checkouts
   and fix forward; switching paying customers back to test mode strands their
   billing. Preserve native login in every recovery release.
