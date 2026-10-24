# Live Stripe Tax activation handoff

TS-954 must be proved against Stripe **test mode** before live activation. The implementation and HTTP fixture checks do not establish finalized invoice/PDF compliance; the real test proof is blocked on the account eligibility described below. No live seller identity, tax registration or credential is invented or installed by this work.

## One user step before live tax invoices

Until this account is eligible, Checkout Sessions collect the billing address, name and tax IDs but do not request `automatic_tax`: Stripe refuses every session that asks for it on an ineligible account (TS-981, 2026-10-24, checkout answered 503). Turning `automatic_tax` on in `StripeBilling.StartCheckoutAsync` is part of finishing this handoff.

The current delivered **test** account is Brazilian. On 2026-10-24, Stripe returned HTTP 400 from `GET /v1/tax/settings`: “Stripe Tax isn't yet supported for your country. Please contact stripe-tax-support@stripe.com to register your interest or if you want to collect tax in a supported market.” The same test credential succeeds on account and portal reads. This blocks the real Stripe test invoice/PDF proof as well as live activation. Ask Stripe whether this actual account can be enabled, or supply an existing eligible test account through the secure credential-installation workflow. Do not change a business country or invent an entity to bypass eligibility. No further retries of the unsupported endpoint are useful until eligibility changes.

In the Stripe Dashboard, select the TerraScale account and **live mode** (leave test/sandbox mode):

1. Open **Settings → Business → Business details**. Confirm the invoice seller's registered legal name, registered business address and business country. Use the actual invoicing entity, not only the TerraScale trading name.
2. Open **Tax → Get started / Settings** (`https://dashboard.stripe.com/tax/settings`). Complete Stripe Tax setup, confirm the head-office address, and select the correct product tax category for the database/SaaS service and the desired tax-exclusive pricing behavior.
3. Open **Tax → Registrations** (`https://dashboard.stripe.com/tax/registrations`). Add only jurisdictions where the entity is actually registered to collect tax, with their effective dates. A Stripe Tax registration does not register the business with a government.
4. Open **Settings → Billing → Invoices** (`https://dashboard.stripe.com/settings/billing/invoice`). Add the seller's tax IDs and select the default IDs, or Stripe's automatic tax-ID display where available. Confirm the legal name and address on the invoice template. Stripe Tax determines when reverse charge applies; do not add an unconditional reverse-charge footer to every invoice.
5. The existing `setup-stripe` command configures the customer portal to let customers edit their name, billing address and tax IDs, as well as payment details. Run its live step only through the already approved live Stripe installation workflow. The live key remains the separate TS-812 user handoff; do not send a key here.

Send these **non-secret** fields to COS in one reply:

- Seller's exact registered legal name, business country and full registered invoice address.
- Seller tax-ID type/country and number(s) that should appear on invoices.
- Tax registration jurisdictions and effective dates; confirm those registrations are real.
- Selected Stripe product tax category and confirmation that prices are tax-exclusive.
- Confirmation that Stripe Tax setup is complete in live mode, seller invoice tax IDs/defaults are set, and the invoice template shows the intended entity.
- Stripe's confirmation of this account's Tax eligibility, or which existing eligible test account to use for the invoice/PDF proof. Any replacement test credential must use the existing secure installation workflow; it must never be sent in chat.

Options: **1) Complete the settings and send the fields (recommended)** — permits live invoice validation after the existing live-key installation. **2) Continue test-mode proof only** — engineering continues, and live tax invoices stay blocked on this named user step.

## Test evidence required

Use a disposable `+tag` customer/organization, a Stripe test card, a full EU address and Stripe's documented test VAT ID. Complete the application-created Checkout Session; inspect the finalized invoice and its PDF for seller identity, customer VAT ID and the tax treatment Stripe actually calculated. A missing registration / `not_collecting` zero-tax result is not proof of reverse charge. Keep fixture seller identity clearly labeled as test data. Never use a real card or charge a live customer.

References: [Checkout automatic tax](https://docs.stripe.com/tax/checkout), [customer portal configuration](https://docs.stripe.com/customer-management/configure-portal), [seller invoice tax IDs](https://docs.stripe.com/invoicing/taxes/account-tax-ids), [zero tax and reverse charge](https://docs.stripe.com/tax/zero-tax), [Stripe Tax testing](https://docs.stripe.com/tax/testing).
