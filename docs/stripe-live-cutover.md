# Stripe live cutover — TS-709

The [zero-configuration contract](zero-configuration.md) requires billing to be
always available, with vendor keys stored once as Fly secrets and delivered
as environment variables. This document retains the authorization, data-integrity and
financial acceptance boundaries; it does not introduce a billing enable switch.
The production binary has no demo or billing mode switches. Preparation alone does not authorize charges, refunds or destructive resets.

## Verify the serving target

The production Fly app is `ts-dashboard` (`fly.toml`). The C# application's
fixed public origin is `https://dash.terrascale.tech` (`StripeBilling.Origin`);
the webhook is `https://dash.terrascale.tech/api/v1/billing/stripe/webhook`.
Verify the app, serving Machine, release, secret names/digests, readiness and
native login immediately before any change:

```sh
fly status -a ts-dashboard
fly releases -a ts-dashboard
fly secrets list -a ts-dashboard
curl -fsS https://dash.terrascale.tech/readyz
scripts/smoke-native-login.sh https://dash.terrascale.tech
```

The greenfield rebuild removed the old dashboard machine. The rollout creates
one machine in each of gru, sjc and fra and checks the tested commit and region
from `/health/ready`. State is in TerraBase, never a dashboard volume.

Record machine/release IDs and Fly secret names/digests. Never record values.
Database provisioning belongs to TerraBase startup; identity bootstrap belongs
to ts-auth.

## Fly delivery and provider verification

The vendor-issued Stripe account key and endpoint-specific signing secret are
`STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET` on `ts-dashboard`. Fly injects
them into the application. There is no identity file or external vault.
See [identity-descriptor.md](identity-descriptor.md) for formats and ownership.

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
Fly must deliver the verified credentials automatically; manual secret
import and demo-switch recipes are retired. Existing delivery and switch paths
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
