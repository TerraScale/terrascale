# Stripe billing

The C# billing implementation uses the official Stripe.net 53 SDK through its `StripeClient.V1`
resources. Stripe is the authority for Stripe customer, checkout, subscription,
invoice and payment method facts. The dashboard's `billing`, `stripe_customers`,
`stripe_checkouts` and `billing_changes` tables ([state storage](state-storage.md))
store the organization binding and coordinate checkout and plan-change requests;
reconciliation reads current Stripe state. Signed webhooks trigger reconciliation
and do not grant entitlements from their payloads; every delivery, a redelivered
event ID included, rereads Stripe, and a webhook that races another writer rereads
and saves again instead of overwriting it.

The shared canonical public origin is `https://dash.terrascale.tech`. Checkout
return/cancel URLs, the billing portal return URL and the webhook audit all use
this same origin. The webhook endpoint is
`https://dash.terrascale.tech/api/v1/billing/stripe/webhook`. Configure
one matching enabled endpoint per Stripe account.

## Checkout reservations

Starting a checkout reserves the organization's `billing` row with a new request ID
and `started_at`. Its Checkout Session expires one hour after `started_at`
(`StripeBilling.CheckoutLifetime`), and nobody is sent to a session before it is
saved on its reservation. A reservation with a saved session is replaced only after
that hour, or once Stripe confirmed the session expired. Stripe accepts
an `expires_at` 30 minutes to 24 hours after the session is created, so a
reservation whose session was never saved creates one only in its first 15 minutes
(`CheckoutCreationWindow`), which leaves it at least 45 minutes; a later start
replaces that reservation, whose session nobody was sent to, with a new one. A
replacement is written only while the reservation still has the request ID and the
saved session, or no saved session, that the replacing start read, and answers 409
otherwise; a customer or a session is saved only on the reservation it was created
for. So, as long as Stripe ends each session at its `expires_at`, an organization
never has two sessions open that someone was sent to.
The organization's Stripe customer and the session are both created with the
reservation's request ID in their idempotency keys. Stripe replays a saved answer,
a 500 included, for at least 24 hours under one key, so a failed create blocks only
its own reservation, for at most those 15 minutes, and the next reservation sends a
new request. Concurrent starts of one reservation share its key, and the `billing`
row and `stripe_customers` bind one customer to the organization. A customer create
whose outcome was lost (a timeout, or a 500 after Stripe created the customer)
followed by a start on the next reservation, or two starts on either side of minute
15, can leave a second customer that is never bound. It gets no session or
subscription, and the organization's deletion finds it by its `organization_id`
metadata and ends any session or subscription it holds.

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
See [state storage](state-storage.md) for the open runtime gates.

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

## Closed-month overage ledger

Paid operation and storage overage is frozen only after its UTC month closes.
`billing_months` records the last plan observed before the boundary, including its
name, allowances, unit rates, currency and pricing-version hash. The record uses
stored terms, never a retrospective lookup in the current catalog. A refresh
records the previous month's terms before observing today's catalog. If no
qualifying plan was observed, `billing_skipped_months` permanently closes the
month without a charge and increments the skipped-month counter and log.
Missing storage-contract data also skips the month before either meter freezes.

`billing_overage_charges` has a unique key on organisation, subscription, UTC
month and meter. Each insert freezes usage, allowance, unit rate, currency,
pricing version, plan name, overage quantity, rounded amount and description.
Concurrent inserts read the winning row. The payload cannot change after that
insert; only the provider ID and acceptance time can be recorded. A zero-cent
row closes its meter without a provider object. Usage arriving after freezing
is never charged. `terrascale_billing_overage_late_usage_observations_total`
counts observations with usage above the frozen value, including repeated
observations; it is not a unique count of late units.

Before dispatching any nonzero row without a provider ID, the reporter lists
all of that customer's invoice items and matches `terrascale_charge=<row ID>`.
It records an existing match rather than creating another item, including after
the provider's idempotency cache expires. Creation uses the immutable row's
quantity, rate, currency and description, with key
`TerraScale.Dashboard/overage-<row ID>`. The row ID uniquely identifies the
organisation, subscription, month and meter. Two dispatchers send the same key
and payload. The provider item also carries organisation, month and meter
metadata. Accepted provider objects are never updated or deleted by the reporter.

The hourly pass records boundary observations and dispatches pending ledger
rows. The daily pass freezes closed-month usage; failures remain pending per
row and neither block nor reopen later months. There is no progress watermark.
Monthly scans begin at `billing.overage_starts_at`, set to the first full UTC month after deployment for existing subscriptions
and the first observation month for new customers. Months before deployment
are never charged by this code. The deployment month is also excluded for
existing customers: the previous reporter may already have created items for
it. That month's unreported overage is forgiven rather than risking a second
charge. Resubscription cannot move this initial cutoff backwards. A missing historical
observation is skipped permanently. Annual subscriptions collect overage monthly.
The billing page estimates current usage; the current month has no provider item.

## Payment recovery and event receipts

A payment failure's deadline is the end of UTC calendar day `failure day + 7
days` (23:59:59.999 UTC). For example, a failure on January 1 ends grace on
January 8 at that time. Duplicate or late failures never extend an existing
deadline. A failure for an invoice whose canonical provider state is paid never
starts grace. Canonical active, trialing or paid state clears it.

A stored deadline alone never changes the published plan. Every 30 seconds,
the reconciler asks the provider before committing an expiry. Paid, active or
trialing state retains the plan and clears grace; still unpaid expires it once.
Provider unavailability retains the plan and retries next pass, increments
`terrascale_billing_grace_expiry_deferred_provider_unavailable_total`, and after
24 hours emits at most one warning per customer per UTC day. The first failure
time and last warning day are persisted. The migration gives existing
past-due/unpaid customers the same full grace starting on migration day.
Stored `billing_required` does not distinguish every restriction reason: paid
subscriptions marked active/trialing with that ambiguous restriction also get
grace. Explicit paused, canceled, expired-incomplete and internal billing
restrictions are left alone.

Canonical entitlement and grace changes commit before optional automatic-tax
repair. API refusals, transport errors and timeouts in that repair are logged,
counted by `terrascale_billing_tax_repair_failed_total`, and attempted on a later
canonical refresh. New checkouts always collect automatic tax; existing
subscriptions without the automatic-tax metadata retain their policy.

Signed provider event IDs have durable unique receipts. A completed receipt
acknowledges a replay without applying billing effects. In-flight duplicates
are also acknowledged without work. Receipts retain the validated event's
routing fields, not its payload; the 30-second reconciler recovers incomplete
work using a five-minute processing lease if a process dies. Processing has a
four-minute cancellation budget, shorter than that lease. Failed work releases
its lease and recovery rereads canonical state. Daily cleanup removes receipts
older than 30 days after receipt once no processing lease is active, covering
automatic retries (three days) and manual resend (up to 30 days).
Events created more than 30 days ago are
acknowledged without effects, so cleanup cannot reopen old events.

## Schema rollout and rollback

The unreleased grace, progress and monthly-plan migrations are replaced by
migrations for the final tables. Apply them only after draining every previous
dashboard instance, then start the new build. There is no dashboard background
job leader or single-runner barrier: jobs run in every instance. New expiry
work starts only with the new instances. This ordering closes the mixed-build
window but requires a dashboard maintenance interval.

A schema-first rolling deployment with previous instances still serving is
unsafe: they ignore grace, and their refresh or plan publication can restrict
a customer in grace. Their reporter can also change provider items frozen by
the new build or create items outside the ledger. The exposure lasts from schema application until the last
old instance is stopped; there is no application-enforced upper bound. Do not
claim a 30-second bound: the old job's period bounds detection, not exposure.

Rollback of the application leaves the additive schema and ledger in place.
The previous build ignores grace, receipts and ledger completion. Do not run
its reporting jobs against this schema: it can recreate or change overage
items and restrict customers in grace. Drain all new instances and keep the dashboard
stopped until a corrected build with these invariants is available. There is
no safe supported rollback to the previous dashboard's background jobs.

## Internal billing

Internal billing is a separate report-only usage and spending-cap feature. It
does not create Stripe customers, subscriptions, invoices or charges. Its
estimates and alerts do not suspend service or settle payments. See
[`internal-billing.md`](internal-billing.md).

For production readiness and explicit live financial acceptance boundaries,
see [`stripe-live-cutover.md`](stripe-live-cutover.md).
