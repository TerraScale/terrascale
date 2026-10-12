# Stripe billing

The C# billing implementation uses the official Stripe.net 53 SDK through its `StripeClient.V1`
resources. Stripe is the authority for Stripe customer, checkout, subscription,
invoice and payment method facts. Dashboard state stores the organization
binding and coordinates checkout and plan-change requests; reconciliation reads
current Stripe state. Signed webhooks trigger reconciliation and do not grant
entitlements from their payloads.

The shared canonical public origin is `https://dashboard.terrascale.tech`. Checkout
return/cancel URLs, the billing portal return URL and the webhook audit all use
this same origin. The webhook endpoint is
`https://dashboard.terrascale.tech/api/v1/billing/stripe/webhook`; the older
`/api/auth/stripe/webhook` path is also recognized for compatibility. Configure
one matching enabled endpoint per Stripe account.

## Read-only Stripe audit

Run the app's audit command in an environment where OCI Vault has delivered the
Dashboard machine identity:

```sh
nice -n 19 dotnet run --project TerraScale.Dashboard.csproj -- audit-stripe
```

The fixed identity path is a consumer contract. Automatic Vault delivery and
the C# deployment remain unqualified; this command does not implement delivery.
See [`csharp-migration.md`](csharp-migration.md) for the open runtime gates.

The command takes no options and reads the fixed DashboardIdentity document. It
does not accept keys, webhook secrets, origins, API versions or mode settings on
the command line or from manual audit-specific environment variables. It infers
test or live mode from the delivered secret key prefix. The audit uses Stripe.net
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

Webhook signatures are verified with the endpoint secret in the Vault-delivered
machine identity. Delivery failures remain visible to Stripe for retry. Confirm
successful delivery attempts in Stripe Workbench when validating an endpoint.

## Internal billing

Internal billing is a separate report-only usage and spending-cap feature. It
does not create Stripe customers, subscriptions, invoices or charges. Its
estimates and alerts do not suspend service or settle payments. See
[`internal-billing.md`](internal-billing.md).

For production readiness and explicit live financial acceptance boundaries,
see [`stripe-live-cutover.md`](stripe-live-cutover.md).
