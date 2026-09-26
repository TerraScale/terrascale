# Stripe billing

The dashboard owns plans, checkout, and subscription state. All Stripe code
lives in `internal/features/payments`; the billing state projection lives in
`internal/features/subscriptions`.

## Modes

- **Demo** (`TS_DASHBOARD_PRODUCTION_DEMO=1`, currently required): no Stripe
  client is created and every `STRIPE_*` setting is ignored. Checkout, plan
  changes, and the portal use the in-memory simulator.
- **Live**: `payments.New` loads and validates the settings below once at
  startup and builds one shared stripe-go client. Any invalid setting stops
  startup with a single error listing every problem.

In-place plan changes (upgrade, scheduled downgrade, Personal to Free) are
enabled only when `STRIPE_MODE=test` until the staging gate in
[`stripe-sandbox-dsh207.md`](stripe-sandbox-dsh207.md) passes.

## Settings

| Variable | Value |
| --- | --- |
| `STRIPE_MODE` | `test` or `live` |
| `STRIPE_SECRET_KEY` | Restricted (`rk_<mode>_…`, preferred) or secret (`sk_<mode>_…`) key for that mode |
| `STRIPE_WEBHOOK_SECRET` | Signing secret (`whsec_…`) of this app's webhook endpoint |
| `DASHBOARD_PUBLIC_ORIGIN` | HTTPS origin Stripe redirects back to; plain HTTP only on loopback |
| `STRIPE_PRICE_{STARTER,PRO,BUSINESS}_{MONTHLY,ANNUAL}` | Six distinct recurring USD Price IDs |

Store the key and signing secret as Fly secrets, never in `fly.toml`:

```sh
fly secrets import --stage -a <app> < /private/path/stripe.env
```

A restricted key needs write access to Customers, Checkout Sessions, Customer
portal, Subscriptions, and Subscription schedules, and read access to Invoices
(proration previews). The audit script also reads Prices, Products, Webhook
endpoints, Events, and portal configurations.

## Webhook endpoint

Create one endpoint per app at `<DASHBOARD_PUBLIC_ORIGIN>/api/auth/stripe/webhook`
with the API version stripe-go decodes (`stripe.APIVersion` in
`vendor/github.com/stripe/stripe-go/v*/api_version.go`). Events rendered with
another version are rejected with `400` and logged, because fields such as
`invoice.parent` and per-item billing periods would otherwise be dropped.

Subscribe to exactly the events in `payments.WebhookEvents`:

```text
checkout.session.completed
checkout.session.async_payment_succeeded
checkout.session.expired
customer.subscription.created
customer.subscription.updated
customer.subscription.deleted
invoice.finalized
invoice.paid
invoice.payment_failed
invoice.voided
```

In the customer portal enable payment method updates, invoice history, and
cancellation. Keep plan switching disabled there; the dashboard confirms plan
changes itself.

## Reliability

- Every mutating call sends an idempotency key. stripe-go retries connection
  errors, lock timeouts, `429`, and retryable `5xx` responses up to three
  times with the same key, and each attempt is bounded by a 20-second timeout.
- Webhooks are verified against the raw body, the signing secret, a
  five-minute timestamp tolerance, the API version, and the configured mode.
  Event IDs are recorded after a successful projection, so redeliveries are
  no-ops and failed projections return `5xx` for Stripe to retry.
- Subscription and checkout events re-read the subscription from Stripe and
  project that snapshot, so out-of-order deliveries and portal changes converge
  on the current price, status, and period end. Invoice events are bound by
  customer and need no extra read.
- Checkout Sessions expire after one hour. The local pending checkout uses the
  expiry Stripe returns and is released by `checkout.session.expired`.

## Audit

`scripts/audit-stripe-test.py --online --env-file <file>` checks a test-mode
configuration read-only: prices and products, the webhook URL, events, and API
version, and the portal features. It never prints credentials or customer IDs.
