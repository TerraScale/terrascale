# Stripe live cutover — TS-709

Preparation does not authorize activating live keys. Keep local development
and tests in `TS_DASHBOARD_PRODUCTION_DEMO=1`. Production keeps its existing
TerraBase persistence and native dashboard sign-in.

`TS_DASHBOARD_PRODUCTION_DEMO=1` suppresses Stripe even when TerraBase is
configured. Persistent apps retain their real stores, sessions and ts-auth;
billing stays unavailable. The memory simulator runs only without a configured
backend and cannot grant simulated entitlements to persistent accounts.

## Verify the serving target

The production Fly app is `ts-dashboard` (`fly.toml`). The public origin is
`https://dash.terrascale.tech`; the webhook is
`https://dash.terrascale.tech/api/auth/stripe/webhook`. Verify the app, serving
Machine, release, secret names/digests, readiness and native login immediately
before any change:

```sh
fly status -a ts-dashboard
fly releases -a ts-dashboard
fly secrets list -a ts-dashboard
curl -fsS https://dash.terrascale.tech/health/ready
scripts/smoke-native-login.sh https://dash.terrascale.tech
```

Read-only inspection on 2026-10-07 found Machine `d8d96000b74e78` started in
`gru` on release `v120`; readiness and the native login form returned `200`.
Fly has ten deployed `Payments__Stripe__*` secrets, but no current `STRIPE_*`
or `DASHBOARD_PUBLIC_ORIGIN` settings. The Go application ignores those legacy
names, so billing is unconfigured. Do not add compatibility aliases; provision
the current names after confirmation.

Record Machine/release IDs and Vault secret OCIDs/version numbers in the
ticket. Never record credential values. Reset obsolete application data and
re-bootstrap through the SDK in the existing provisioned database when needed;
do not replace the serving app, cell or volume. Do not touch Forgejo, ts-ops or
homelab hosts.

## Prepare live configuration from OCI Vault

The inspected production Vault is `gru-1-vault`, region `sa-saopaulo-1`, in
compartment `gru-1-control`. No Stripe-named secret appeared in its 155-secret
inventory. The follow-up inventory of `sfo-1-vault` (`us-sanjose-1`, 41 secrets)
also found no Stripe/billing-named secret. **Live credential and signing-secret
OCIDs have not been identified; cutover is NEEDS-USER.** Credentials under
other names or in other scopes remain unverified. Verify them before proceeding.
The working CLI authentication uses `--config-file
/home/mariogk/.oci/homelab-api.config --profile DEFAULT --auth api_key`; the
browser session profile was expired.

1. Select the live Stripe account and verify its business activation. Store
   the restricted live key and this endpoint's live signing secret in OCI
   Vault. Use the permissions in [`stripe.md`](stripe.md); secret/restricted
   keys must start with `sk_live_`/`rk_live_`.
2. Create or identify six **live** recurring USD prices with the existing plan
   amounts, intervals and active products. Configure the live customer portal
   for payment updates, invoices and cancellation; disable portal plan changes.
   Enable Checkout's one-subscription limit.
3. Configure the live webhook at the exact production URL using the vendored
   `stripe.APIVersion` and all `payments.WebhookEvents`. Its signing secret is
   specific to that endpoint; the prefix alone cannot prove it matches.
4. Assemble `KEY=VALUE` settings from the selected CURRENT Vault versions in a
   private file: `STRIPE_MODE=live`, the two secrets, six price IDs, and
   `DASHBOARD_PUBLIC_ORIGIN=https://dash.terrascale.tech`. Include only those
   ten settings; align the separately managed `DASHBOARD_PUBLIC_URL` with the
   same origin. Keep the existing
   production TerraBase scope/credentials. Never source the file as shell code.
5. Run the read-only audit below. It checks object mode, prices/products,
   webhook configuration and portal features. It does not make charges,
   activate keys, or prove that a webhook delivery succeeded.

If those settings are held together in a Vault dotenv bundle, retrieve its
CURRENT version without printing it. Run in Bash with tracing disabled:

```sh
set +x
set -euo pipefail
umask 077
stripe_live_env=$(mktemp)
trap 'rm -f -- "$stripe_live_env"' EXIT
oci --config-file /home/mariogk/.oci/homelab-api.config --profile DEFAULT \
  --auth api_key --region sa-saopaulo-1 secrets secret-bundle get \
  --secret-id "$STRIPE_LIVE_CONFIG_SECRET_OCID" \
  --stage CURRENT --query 'data."secret-bundle-content".content' --raw-output \
  | base64 --decode > "$stripe_live_env"
nice -n 19 python3 scripts/audit-stripe.py --mode live --online \
  --env-file "$stripe_live_env"
```

For an audit without a plaintext file, pipe the same decoded bundle to
`nice -n 19 python3 scripts/audit-stripe.py --mode live --online --env-file -`.
An invalid or mixed-mode configuration fails before contacting Stripe.
Never print the decoded bundle or put secret values in command arguments.

## Test-to-live gate

This preparation does **not** authorize cutover. Replace wall-clock projection
ordering with ordering tied to conditional state saves before approval.
Explicit demo configuration now suppresses Stripe while retaining TerraBase
persistence and keeping simulated billing and security operations unavailable.
Missing billing collections or databases now return retryable webhook failures;
the default persistence HTTP client limits each request to 15 seconds. Exercise
these paths against TerraBase before approval. The existing duplicate-event
tests alone do not establish those guarantees.

1. Complete [`stripe-sandbox-dsh207.md`](stripe-sandbox-dsh207.md), including
   signed deliveries, duplicate/out-of-order events, failed-payment recovery,
   checkout returns and invoice reconciliation. Local regression checks:
   `nice -n 19 make test-stripe-audit` and
   `nice -n 19 env GOMAXPROCS=2 GOFLAGS='-mod=vendor -p=1' go test -race -short -parallel 1 ./internal/features/payments`.
2. Inventory dashboard billing through ts-go/public APIs, without publishing
   customer details. Test Stripe customers, subscriptions, invoices, checkout
   sessions and prices cannot be reused in live mode. Check both
   `stripe_customers`/`stripe_organizations` and organization billing projections.
   Unconfigured billing today does not prove older test references are absent.
3. If any test bindings exist, delete obsolete test data through ts-go and
   re-bootstrap the application with the current schemas in the existing
   provisioned database. The closed beta permits resetting users, organizations
   and sessions; do not retain old records through migrations, backfills or
   compatibility shims. Confirm the resulting billing bindings, checkout
   records and organization entitlements contain no test references. Dashboard
   entity IDs must be plain ULIDs, and uniqueness must use TerraBase native
   constraints rather than claim documents.
4. Require the live audit to pass and verify credentials can read/write the
   existing TerraBase billing collections. Keep in-place plan changes disabled
   in live mode until their separate staging gate is satisfied.
5. Ask the user to confirm **activating live billing on `ts-dashboard`** with
   the verified target, configuration versions, runtime hardening, clean data
   and test evidence. Stop before importing/staging any live Fly secrets: a later normal
   deployment can activate staged secrets automatically.

## Activate only after confirmation

Require `TS_DASHBOARD_PRODUCTION_DEMO` to be absent from production settings.
If it is set as a Fly secret, stage its removal after confirmation with
`fly secrets unset --stage -a ts-dashboard TS_DASHBOARD_PRODUCTION_DEMO`.
Keep the flag absent from `fly.toml`; the deployment check enforces this.

Import the audited Vault-derived configuration into the verified existing app
as one staged set, then deploy the reviewed current image in place:

```sh
fly secrets import --stage -a ts-dashboard < "$stripe_live_env"
fly deploy -c fly.toml --remote-only --strategy immediate --ha=false
```

Preserve production persistence, ts-auth, CSRF and all unrelated settings. Do not
enable the demo simulator in production. Record the resulting release and
Machine IDs and confirm readiness and native login again.

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
