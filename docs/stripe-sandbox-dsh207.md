# DSH-207 Stripe sandbox audit

The dashboard's Stripe checkout and subscription flows use the official
Stripe.net 53 SDK and its `StripeClient.V1` resources. The audit command is
read-only and uses the same automatically delivered DashboardIdentity as the
application. It does not accept a test/live selector, manual key, origin or
other options; key mode is inferred from the delivered key prefix.

## Run the read-only audit

Run this from the dashboard checkout in an environment where ts-orch has
delivered the dashboard identity as OCI Vault files:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

Production runtime secrets come from OCI Vault via instance principal; CI secrets are
GitHub Actions secrets. The complete inventory lives in the terrascale repository
under `ops/secrets/`. This command consumes the identity; it does not provision
it. See [`csharp-migration.md`](csharp-migration.md).

The audit uses the fixed canonical origin `https://dash.terrascale.tech`,
shared by checkout return/cancel URLs, the billing portal return URL and
webhook configuration. It inventories Stripe prices/products, webhook
endpoints/events, recent invoice-created event delivery counts, portal settings,
and subscription mode/status counts. It expects exactly one valid recurring
USD price for each paid plan at its published interval: Starter annual ($30),
Pro monthly and Team monthly, or three unique paid plan prices total. The inventory does not identify
which dashboard organization owns a Stripe customer. Per-endpoint delivery
success must be checked in Stripe Workbench.

The command reports observations from the account it reads. This document does
not claim that a current sandbox or live audit has passed. A read-only audit
does not exercise signed webhook delivery, checkout completion, card failure,
upgrades, downgrades, renewal or trial expiry; those require separate verified
acceptance evidence before a staging gate can be considered complete.

## OCI runtime

Use only a separately authorized sandbox identity; the production dashboard
runs on OCI gru under ts-orch. This runbook authorizes neither new environments
nor changes to production credentials. See [OCI deployment](oci-deployment.md).
The fixed audit origin remains canonical even from a development checkout;
the audit command has no origin override.

Internal billing is a separate report-only usage estimate and cap. It does not
create Stripe subscriptions or charges, and does not suspend service. It is not
part of Stripe sandbox payment acceptance; see [`internal-billing.md`](internal-billing.md).
