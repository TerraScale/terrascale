# Stripe billing

The C# billing implementation uses the official Stripe.net 53 SDK through its `StripeClient.V1`
resources. Stripe is the authority for Stripe customer, checkout, subscription,
invoice and payment method facts. Dashboard state stores the organization
binding and coordinates checkout and plan-change requests; reconciliation reads
current Stripe state. Signed webhooks trigger reconciliation and do not grant
entitlements from their payloads.

The shared canonical public origin is `https://dash.terrascale.tech`. Checkout
return/cancel URLs, the billing portal return URL and the webhook audit all use
this same origin. The webhook endpoint is
`https://dash.terrascale.tech/api/v1/billing/stripe/webhook`; the older
`/api/auth/stripe/webhook` path is also recognized for compatibility. Configure
one matching enabled endpoint per Stripe account.

## Read-only Stripe audit

Run the app's audit command in an environment where ts-orch has delivered the
dashboard secrets as OCI Vault files:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- audit-stripe
```

The secret names and formats are in [`identity-descriptor.md`](identity-descriptor.md).
Production runtime secrets come from OCI Vault via instance principal; CI secrets are
GitHub Actions secrets. The complete inventory lives in the terrascale repository under
`ops/secrets/`. This command consumes secrets; it does not provision them.
See [`csharp-migration.md`](csharp-migration.md) for the open runtime gates.

## Test account setup

`setup-stripe` creates what the audit expects in a Stripe test account: one
`TerraScale <Plan>` product (metadata `plan`) per paid plan, its monthly and
annual USD prices from `PlanCatalog` (lookup keys `terrascale_<plan>_<interval>`),
and the default billing portal (payment method update, invoice history and
cancel at period end on; subscription update off). It archives duplicate
matching prices. A second run changes nothing. It refuses live keys.

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- setup-stripe
```

The command takes no options and reads DashboardIdentity from ts-orch-delivered secret files. It
does not accept keys, webhook secrets, origins, API versions or mode settings on
the command line or from manual audit-specific environment variables. It infers
test or live mode from the delivered standard (`sk_`) or restricted (`rk_`) secret key prefix. The audit uses Stripe.net
53 `V1` list/retrieve calls only; it does not create or modify Stripe objects,
charge customers, or switch the running application's billing mode.

The JSON report checks the delivered key and webhook secret formats, the
canonical HTTPS origin, the webhook URL/API version/required events, recent
`invoice.created` events and their pending-delivery counts, the default portal
features, subscription key-mode consistency, and subscription status counts.
It also inventories active recurring USD prices and products. For each paid
plan/interval pair it expects exactly one matching active price: Starter, Pro
and Business, monthly and annual, for six unique prices total. Subscription
counts are inventory only; the report does not infer a TerraScale organization
from a Stripe customer. Check endpoint delivery attempts in Stripe Workbench;
the public Events API cannot verify delivery success to a particular endpoint.

The audit reports current observations when run. No live or sandbox audit result
is asserted here. A passing read-only inventory is not evidence that signed
webhook delivery, checkout completion, payment, renewal or plan changes have
been exercised.

## Webhook events

Subscribe to the events consumed by the dashboard reconciliation path:

```text
checkout.session.completed
checkout.session.async_payment_succeeded
checkout.session.async_payment_failed
checkout.session.expired
customer.subscription.created
customer.subscription.updated
customer.subscription.deleted
customer.subscription.paused
customer.subscription.resumed
customer.subscription.pending_update_applied
customer.subscription.pending_update_expired
customer.subscription.trial_will_end
invoice.finalized
invoice.paid
invoice.payment_succeeded
invoice.payment_failed
invoice.payment_action_required
invoice.marked_uncollectible
invoice.voided
```

Webhook signatures are verified with the endpoint secret in the OCI Vault-delivered
machine identity. Delivery failures remain visible to Stripe for retry. Confirm
successful delivery attempts in Stripe Workbench when validating an endpoint.

## Internal billing

Internal billing is a separate report-only usage and spending-cap feature. It
does not create Stripe customers, subscriptions, invoices or charges. Its
estimates and alerts do not suspend service or settle payments. See
[`internal-billing.md`](internal-billing.md).

For production readiness and explicit live financial acceptance boundaries,
see [`stripe-live-cutover.md`](stripe-live-cutover.md).
