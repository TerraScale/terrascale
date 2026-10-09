# Stripe billing

The dashboard owns plans, checkout, and subscription state. All Stripe code
lives in `internal/features/payments`; the projection onto organizations lives
in `internal/features/subscriptions`.

## Sync model

Billing follows [t3dotgg/stripe-recommendations](https://github.com/t3dotgg/stripe-recommendations):
Stripe is the source of truth, and one function, `(*payments.Stripe).SyncCustomer`,
copies a customer's complete state out of it. Webhook payloads are never
projected; an event only says which customer to sync.

1. Before the first checkout, the dashboard creates the Stripe customer (with
   `organization_id` metadata) and stores the customer ↔ organization binding
   create-only, so an organization has exactly one customer.
2. Every checkout, portal session, and plan change uses that customer.
3. `SyncCustomer` reads the customer, their subscriptions (with payment method
   and schedule), recent invoices, and the pending Checkout Session. It saves
   the result as one `CustomerState` and projects it onto the organization's
   plan, status, renewal, scheduled or pending change, card, and invoices.
4. It runs on every tracked webhook, eagerly on the checkout return (customers
   usually come back before the webhooks), and after each plan change.

The save is conditional on the version loaded before reading Stripe. When two
syncs race, the loser re-reads Stripe, so the stored state always comes from a
read that started after the previous save. Each successful save advances the
customer's revision atomically with its snapshot. The organization applies
revisions in order within its own conditional update; a stale projection
re-reads Stripe and retries. Replica clock skew cannot discard newer saved
billing. `SyncedAt` is diagnostic metadata only. Duplicate, late, and out-of-order
events converge without an event ledger; exhausted retries return an error so
Stripe can redeliver.

## State store

Live mode stores synced state in the dashboard's own TerraBase database through
ts-go. Local demos without a backend keep everything in memory and never contact
Stripe or TerraBase.

| Collection | Document ID | Contents |
| --- | --- | --- |
| `stripe_organizations` | organization ID | `customerId` (unique), written create-only |
| `stripe_customers` | Stripe customer ID | `organizationId` (unique) and the synced `CustomerState` |

The collections belong to the dashboard's provisioned TerraBase database.
`payments.New` requires that database handle. A missing customer document is an
unbound customer; its webhook returns `200` without contacting Stripe. State
read/write errors, including missing collections or databases, return a retryable
webhook failure instead of acknowledging an unsynced delivery.

## Modes

- **Demo** (local `TS_DASHBOARD_PRODUCTION_DEMO=1`, with no TerraBase connection): no Stripe
  client is created and every `STRIPE_*` setting is ignored. Checkout, plan
  changes, and the portal use the in-memory simulator.
- **Provider billing** (TerraBase configured, demo flag unset): `payments.New`
  loads and validates the settings below once at
  startup and builds one shared stripe-go client. Any invalid setting stops
  startup with a single error listing every problem.

The explicit demo flag always disables Stripe. With TerraBase configured, the
application retains persistent stores and native sign-in; billing is unavailable
and the simulator cannot grant simulated entitlements into persistent state.
The memory simulator runs only without a configured backend.

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
| `TERRABASE_URL`, `TERRABASE_API_KEY`, `TERRABASE_PROJECT_ID`, `TERRABASE_DATABASE` | Existing dashboard TerraBase connection; billing uses the same ts-go database handle |

The application supplies `TS_BILLING_TENANT_ID`, `TS_BILLING_PROJECT_ID` and
`TS_BILLING_DATABASE_ID` internally from that provisioned scope. Stripe
cutover does not create or select a separate billing database.

Store the key, signing secret, and TerraBase token as Fly secrets, never in `fly.toml`:

```sh
fly secrets import --stage -a <app> < /private/path/stripe.env
```

A restricted key needs write access to Customers, Checkout Sessions, Customer
portal, Subscriptions, and Subscription schedules, and read access to Invoices
(proration previews). The audit script also reads Prices, Products, Webhook
endpoints, Events, and portal configurations.

## Webhook endpoint

The former `/api/auth/stripe/webhook` URL remains a compatibility alias with the
same signature validation and customer sync. Keep only one Stripe endpoint
configured per app, using either URL, to avoid duplicate deliveries.

Create one endpoint per app at `<DASHBOARD_PUBLIC_ORIGIN>/api/v1/billing/stripe/webhook`
with the API version stripe-go decodes (`stripe.APIVersion` in
`vendor/github.com/stripe/stripe-go/v*/api_version.go`). Events rendered with
an incompatible API release train are rejected with `400` and logged, because
fields such as
`invoice.parent` and per-item billing periods would otherwise be dropped.

Subscribe to exactly the events in `payments.WebhookEvents`. Each one only
triggers a sync of its customer:

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

## Stripe dashboard checklist

- **Settings → Checkout → Subscriptions: enable "Limit customers to one
  subscription".** Only this stops two open Checkout Sessions from creating two
  subscriptions. A sync that finds more than one current subscription logs an
  error.
- Checkout offers only cards (including Apple Pay and Google Pay) and Link,
  set in code with `payment_method_types`. Cash App Pay and similar methods
  cause most abandoned and disputed payments.
- In the customer portal enable payment method updates, invoice history, and
  cancellation. Keep plan switching disabled there; the dashboard confirms
  plan changes itself.

## Reliability

- Every mutating call sends an idempotency key. stripe-go retries connection
  errors, lock timeouts (including lock-timeout `429`), and retryable `5xx`
  responses up to three times with the same key. Ordinary rate-limit `429`
  errors return to the caller. Each attempt has a 20-second timeout.
- Webhooks are verified against the raw body, the signing secret, a
  five-minute timestamp tolerance, the API version, and the configured mode.
  A failed sync returns `5xx`, so Stripe redelivers; syncing is idempotent.
  Events without a customer, for an unbound customer, or for a deleted
  organization are acknowledged and skipped.
- Checkout Sessions expire after one hour. The local pending checkout uses the
  expiry Stripe returns and is released by the next sync after it expires.

## Audit

`scripts/audit-stripe.py --online --env-file <file>` checks a test-mode
configuration read-only: prices and products, the webhook URL, events, and API
version, and the portal features. Use `--mode live` to check live configuration;
the default remains test. `--env-file -` accepts settings piped from OCI Vault.
Explicit file/stdin settings are audited alone, without filling missing values
from the environment. The default portal configuration must satisfy the feature
checks, because portal sessions use it. Invalid
local settings prevent online requests. It never prints credentials or customer
IDs and never switches the running app's mode.

Follow [`stripe-live-cutover.md`](stripe-live-cutover.md) for the production
targets, Vault handoff, confirmation gate, and test-to-live checklist.
