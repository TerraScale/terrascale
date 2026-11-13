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

Overage is charged only on terms the provider establishes for the organisation's
UTC month, never on what the dashboard happened to observe or when.

**Terms timeline.** Every successful billing refresh records the provider's facts
for each subscription in `billing_subscriptions` (start from `start_date`, end
from `ended_at`, status, and `checked_at`, the time of that refresh) and appends
terms to `billing_terms` with the provider's effective time:

| Basis | Effective time | Terms |
| --- | --- | --- |
| `start_date` | the subscription's `start_date` | the price in the `customer.subscription.created` event, or the current price when the provider holds no change event since the start |
| `event` | the event's `created` time | the price in each `customer.subscription.updated` event's subscription snapshot |
| `first_check` | the start of the provider's event window (29 days before the refresh) | the current price, when the window holds no change event |
| `unestablished` | the earliest time the change could have taken effect | the price the refresh saw, when no provider event explains it, or when the history before the event window is gone |

The refresh lists `customer.subscription.created` and `customer.subscription.updated`
events created since that subscription's last `checked_at`, so a change learned
late keeps its provider time: an upgrade on the 30th processed on the 2nd counts
for the earlier month. `learned_at` only records when the dashboard read it. A
price that maps to no plan records terms without a pricing version. Recording
terms never blocks the entitlement refresh: it runs after the binding commits,
and a failure is logged and counted by `terrascale_billing_terms_unrecorded_total`.

**Freezing a month.** `billing.overage_starts_at` is the organisation's first
unsettled month. A month is frozen only when every subscription the organisation
had in it was read by a refresh at or after the month's end (or after the
subscription ended), and the binding's current subscription has been recorded.
Until then it waits, and the daily pass publishes
`terrascale_billing_overage_months_waiting_for_terms` and
`terrascale_billing_overage_oldest_waiting_month_seconds`. A month without any
subscription moves the start to the next subscription's month.

The candidates are, for every subscription of the organisation that overlaps the
month, the terms in effect when the month (or the subscription) began and every
terms change before the month (or the subscription) ended. A subscription
canceled on the 20th and its replacement started on the 22nd both count. Usage is
measured per organisation and month, so each meter freezes the lowest integer-cent
charge over the candidates; operations and storage can select different terms.
If a candidate is `unestablished`, a part of the month has no terms, or a
candidate's price is unmapped, the row keeps its calculable amount with
`skip_reason` `terms_unestablished` or `missing_plan` and is never dispatched.
A meter without usage or without the storage contract is recorded in
`billing_skipped_meters` with its reason; the other meter still freezes in its
own row. The organisation's newest overlapping subscription owns the charge.

`billing_overage_charges` has a unique key on organisation, UTC month and meter,
so two subscriptions in one month can never produce two charges. Each insert
freezes usage, allowance, rate, currency, pricing version, plan name, quantity,
rounded amount and the readable description; only `state`, the provider fields,
`skip_reason` and the claim can change afterwards. States:

| State | Meaning |
| --- | --- |
| `pending` | waiting for dispatch |
| `charged` | the provider holds the ledger's item; `provider_id` and `provider_amount_cents` record it |
| `adopted` | the previous reporter already charged this month and meter; the row records its item and total beside the ledger's own amount, and nothing more is charged or refunded |
| `mismatch` | the provider holds an item for this row whose amount, customer or currency differs; held for review, never retried, counted by `terrascale_billing_overage_mismatch_total` |
| `skipped` | not charged, with the amount kept and `skip_reason` |
| `nothing_due` | the lowest charge is zero |

**Dispatch.** A dispatcher first claims a pending row (`claim_owner`,
`claim_until` ten minutes ahead); a row whose claim is live is left alone. One
attempt runs under a four-minute cancellation budget, far below the provider's
24-hour idempotency-key retention; an abandoned attempt releases the claim and
the next pass starts with a fresh lookup. Inside the claim, immediately before
any create, it lists the customer's invoice items created since the month began
(every page) and:

1. records an item tagged `terrascale_charge=<row ID>` as `charged`, or as `mismatch` if it differs;
2. adopts items of the previous reporter, identified by `terrascale_kind`,
   `terrascale_month` and `terrascale_org` without `terrascale_charge`;
3. retires the row as `skipped` with `subscription_ended` when the subscription
   the charge belongs to has ended, so a crash after the provider accepted an
   item is recorded as `charged` even after a cancellation;
4. otherwise creates the item with the exact integer amount, currency,
   description and key `TerraScale.Dashboard/overage-<row ID>`.

The result is saved only while the same claim is held, so retirement and
dispatch of one row never interleave. The daily pass settles at most three
months, reads at most 200 usage pages and dispatches at most 500 rows; it reads
bindings and rows in pages and resumes an unfinished usage scan where it
stopped. The hourly pass only dispatches. Annual subscriptions collect overage
monthly. The billing page estimates current usage; the current month has no
ledger row.

**Mixed builds.** The reporter, binding refresh, plan reconciler, event-receipt
recovery and grace reconciliation first compare the database's applied
migrations with the ones the build knows. If the database has one the build
lacks, they log once and stay idle, so a release or rollback never runs billing
jobs on a schema they do not understand.

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

## Releasing this change

The schema changes only add tables and columns. The build running today has no
migration check, so during this first rollout old instances keep working beside
new ones until the last one is replaced. Release between the 4th and the last
day of a UTC month and replace every instance in every region within one day;
outside that window an old instance also revisits the previous month.

What an old instance can still do while it runs:

- create or update the current month's invoice items with its own metadata
  (`terrascale_kind`, `terrascale_month`, `terrascale_org`, no
  `terrascale_charge`; quantity times unit amount). The ledger adopts those
  items when the month freezes instead of charging again, also for bindings
  created during the rollout;
- restore `billing_required` on an organisation the new build placed in
  payment grace. The next refresh of the new build puts it back in grace.

Existing subscriptions start the ledger at the month after the deployment
month, which the previous reporter has already charged.

After the last instance is replaced, run these read-only checks. The first two
must return no rows; the last two list what to review:

```sql
SELECT id, billing_status, payment_grace_until
FROM organizations
WHERE payment_grace_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
  AND billing_status <> 'active';

SELECT organization_id
FROM billing
WHERE kind = 'stripe' AND subscription_id <> ''
  AND (overage_starts_at IS NULL OR julianday(overage_starts_at) <= julianday('0001-01-01'));

SELECT organization_id, month, meter, amount_cents, provider_id, provider_amount_cents
FROM billing_overage_charges
WHERE state = 'adopted'
ORDER BY month, organization_id;

SELECT id, billing_status, payment_grace_until
FROM organizations
WHERE payment_grace_until IS NOT NULL
  AND billing_status = 'billing_required'
ORDER BY payment_grace_until;
```

The last query lists organisations restricted while they had a grace deadline,
including deadlines already past, which an old instance may have restricted
before the new build asked the provider. A row in the first or last query is
repaired by that organisation's next refresh (daily, or on its next provider
event). Rollback: before any month
has frozen in the ledger, the previous build can return. After a row exists in
`billing_overage_charges`, roll forward only: the previous reporter does not
read the ledger and would charge those months again. Future releases and
rollbacks are protected by the migration check.

## Internal billing

Internal billing is a separate report-only usage and spending-cap feature. It
does not create Stripe customers, subscriptions, invoices or charges. Its
estimates and alerts do not suspend service or settle payments. See
[`internal-billing.md`](internal-billing.md).

For production readiness and explicit live financial acceptance boundaries,
see [`stripe-live-cutover.md`](stripe-live-cutover.md).
