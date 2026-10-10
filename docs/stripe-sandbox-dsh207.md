# DSH-207 Stripe sandbox audit

This change adds plan selection and review to `/usage?tab=billing`. The Go
dashboard can show the flow in demo mode. The live Stripe adapter supports
upgrades on the existing subscription, scheduled downgrades and interval
changes, Personal cancellation to Free, undo, invoice projection, and a
one-time Checkout trial. Plan changes are limited to Stripe test mode.
The seeded paid-plan limits now come from the prior onboarding catalog:
Starter has 5 databases, 1M operations/month, 5 GB-region and 5 members;
Pro has 25, 100M, 25 GB-region and 25; Business has 100, **50M**,
5 TB-region and 100. The owner confirmed the 50M Business value for DSH-207.
Free limits are not fully published in the current source; the known one
database cap is enforced for new databases while existing data is retained.

## Reproduce the read-only configuration audit

```sh
nice -n 19 python3 scripts/audit-stripe.py --online --env-file /path/to/local/test.env
```

The file uses the `STRIPE_*` names documented in [`stripe.md`](stripe.md). It
never prints keys, webhook secrets, customer IDs, or subscription IDs. It
checks six active recurring USD prices and their products, portal features,
matching webhook URL, events, and API version, and inventories
subscription statuses and duplicate current subscriptions per customer. A
customer count does not prove which TerraScale organization owns a Stripe
subscription; attribution requires the account's stored customer and
subscription IDs.

The read-only run on 2026-09-25 found all six price/product checks passing,
the portal active, one matching enabled webhook, and no customer with multiple
current subscriptions among 6 active and 5 trialing subscriptions. The
webhook lacks at least one required invoice event. The configured public
origin is a Tailscale development host; it is not the Fly staging app.
Per-endpoint delivery success must be checked in Stripe Workbench because the
public event list does not prove HTTP delivery to a particular endpoint.

A disposable test customer and subscription confirmed that the Stripe test API
accepts an invoice proration preview, an immediate Starter-to-Pro update, a
scheduled Pro-to-Starter change, and releasing that schedule. The temporary
resources were deleted afterward. A separate test clock advanced a 30-day
trial through expiry and renewal. The subscription became active, but invoices
were still in draft at the advance boundaries; only one recurring invoice was
paid by day 63. This does not pass the invoice or webhook delivery gate. At
least one enabled test webhook subscribes to `invoice.created`. Confirm its
delivery attempts in Stripe Workbench before attributing the invoice delay.
The ten most recent `invoice.created` events all reported one pending webhook
delivery in the API when checked on 2026-09-25; the API does not identify
which endpoint is pending.

## Staging gate

The Go runtime now has TerraBase-backed account, session and throttle storage
through ts-go, and headless ts-auth integration. Dashboard sign-in remains on
its own password/email-code pages; only SSO redirects to hosted authorization.
Production uses the existing `TERRABASE_*`, `TS_AUTH_*` and
`TS_DASHBOARD_CSRF_SECRET` configuration. Billing uses that same database.
Local demo runs belong only in isolated test binaries and do not satisfy the
Stripe staging gate.

Verify the existing dev target and configure a dedicated test origin and
webhook with all required events. Require successful signed deliveries and
durable state after restart. Run Checkout, card failure, upgrades, downgrades,
Free, cancellation, renewal and trial expiry with Stripe test clocks against
that app. The 2026-09-25 evidence above is historical; repeat these checks
against the current runtime before passing the gate.

The operator steps for the dev app are in
[`fly-stripe-dev-dsh207.md`](fly-stripe-dev-dsh207.md).
