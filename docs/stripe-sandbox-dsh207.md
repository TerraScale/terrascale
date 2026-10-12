# DSH-207 Stripe sandbox audit

The dashboard's Stripe checkout and subscription flows use the official
Stripe.net 53 SDK and its `StripeClient.V1` resources. The audit command is
read-only and uses the same automatically delivered DashboardIdentity as the
application. It does not accept a test/live selector, manual key, origin or
other options; key mode is inferred from the delivered key prefix.

## Run the read-only audit

Run this from the dashboard checkout in an environment where OCI Vault has
delivered the dashboard identity:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

Automatic delivery of the fixed identity and deployment of the C# application
remain open qualification gates. This command consumes the identity; it does
not create or deliver it. See [`csharp-migration.md`](csharp-migration.md).

The audit uses the fixed canonical origin `https://dashboard.terrascale.tech`,
shared by checkout return/cancel URLs, the billing portal return URL and
webhook configuration. It inventories Stripe prices/products, webhook
endpoints/events, recent invoice-created event delivery counts, portal settings,
and subscription mode/status counts. It expects exactly one valid recurring
USD price for each paid plan and interval: Starter, Pro and Business, monthly
and annual, or six unique paid plan prices total. The inventory does not identify
which dashboard organization owns a Stripe customer. Per-endpoint delivery
success must be checked in Stripe Workbench.

The command reports observations from the account it reads. This document does
not claim that a current sandbox or live audit has passed. A read-only audit
does not exercise signed webhook delivery, checkout completion, card failure,
upgrades, downgrades, renewal or trial expiry; those require separate verified
acceptance evidence before a staging gate can be considered complete.

## Fly dev environment

The dev app remains `terrascale-dashboard-dev`. Use the existing Fly app and
its automatically delivered identity; do not add dotenv files or manually
copy vendor credentials into Fly settings. See
[`fly-stripe-dev-dsh207.md`](fly-stripe-dev-dsh207.md) for app readiness and
deploy checks. The fixed audit origin is the canonical production origin even
when the command runs from a development checkout; the command does not provide
an origin override.

Internal billing is a separate report-only usage estimate and cap. It does not
create Stripe subscriptions or charges, and does not suspend service. It is not
part of Stripe sandbox payment acceptance; see [`internal-billing.md`](internal-billing.md).
