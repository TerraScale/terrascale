# Payment webhook and billing-domain audit — 2026-10-13

Review the two verified defects first. The two Free-setup failures below are **unresolved business expectations**, not established pricing defects. Product source was inspected read-only; patches are proposals only.

- Audited source, initial checkout HEAD, and fetched `origin/main`: **`b719878083dffd9441e063bbfaccfa0dffa95abd`**.
- At packaging, the shared cached `origin/main` ref had advanced to `015b42c4825866131a00d48469650bcba04b2364`. A read-only diff confirmed no changes to `Features/Billing`, `AccountModels.cs`, `DatabaseService.cs`, or `docs/stripe.md` between these SHAs. The bound source remains pinned to the audited SHA; publication still requires checking the latest base.
- Delivery branch: `audit/payment-webhook-correctness-20261013`. The local delivery commits change only this audit's new test/report/evidence files; current HEAD is available with `git rev-parse HEAD`. Screenshot metadata retains the actual earlier capture HEAD, not the later packaging HEAD.
- Actual implementation: .NET 10, official Stripe.net 53, `Features/Billing`, and the existing `TerraScale.Dashboard.E2E` TUnit project. No `.agents/skills` directory exists in this checkout. No replacement payment system, dependency, or source refactor was introduced.
- Publication is held for the coordinator's serialized CI slot. Alpha CI is permitted with release-critical jobs retaining priority. No PR, push, CI change, deployment, live payment, account operation, or real credential retrieval was performed.

All source path/line citations below refer to the audited SHA above.

## Verified defects

### P1 — An unmapped active Stripe price retains paid database capacity

**Expected contract:** `src/TerraScale.Dashboard/Features/Billing/StripeBilling.cs:268–275` explicitly says to “revoke an unqualified plan even when this organization previously held paid entitlements.” Personal fallback to Free is established at `StripeBilling.cs:279–283`. `Features/Billing/PlanCatalog.cs:21–22` and `:67–68` configure one Free database and five Starter databases. These existing values are used unchanged.

**Observed:** A Personal owner with a valid Starter subscription creates one database. Current Stripe state then reports an active subscription with an EUR price; the customer, subscription ownership, product, and quantity remain bound. EUR is a valid Stripe currency but fails this dashboard's USD plan qualification. A valid webhook returns `200`, clears `BillingBinding.EffectivePlan`, and sets `billingStatus=billing_required`. The next database creation still returns **`201 Created`** instead of Free's **`409 Conflict`** limit response.

**Cause:** `StripeBilling.cs:273` changes billing status without changing the old organization `PlanId`. `Features/Accounts/AccountModels.cs:57` permits a Personal owner to provision independently of billing status. `Features/Databases/DatabaseService.cs:208–221` consequently still enforces Starter's five-database limit. This is paid-capacity retention after explicit price disqualification, rather than a proposal to alter dunning or role policy.

**Local repro:** `UnmappedActivePriceCannotRetainPaidDatabaseCapacity` exercises `POST /billing/checkout`, `GET /billing/checkout/return?session_id=…`, `POST /api/v1/billing/stripe/webhook`, and `POST /api/v1/dashboard/databases`. The database backend is the existing local synthetic provider. Existing databases are preserved.

**Minimal proposal:** In the unmapped active/trialing branch only, set a Personal organization's `PlanId` to `OrganizationRecord.FreePlanId`. Preserve the canonical Stripe subscription binding, the payment-required status, and existing databases. See [proposed-source-fixes.patch](proposed-source-fixes.patch). No change to shared authorization code is proposed.

### P2 — Zero-decimal invoice amounts display 100× too small

**Expected provider contract:** [Stripe's currency and API amount specification](https://docs.stripe.com/currencies#zero-decimal) states that JPY and CLP amounts are already in whole currency units. For example, an API amount of `500` JPY means `500` JPY, without multiplication or division. The published contract was read during this audit. ISK and UGX retain Stripe's special two-decimal API representation; HUF/TWD charge amounts must not use their payout-specific scale.

**Observed:** `GET /organization?tab=billing` displays an invoice amount of `1500` JPY as **`JPY 15.00`** and `1234` CLP as **`CLP 12.34`**. The USD control (`501` → `USD 5.01`) passes.

**Cause:** `Features/Billing/StripeBillingInvoices.cs:28` correctly preserves Stripe's integer amount and currency. `Features/Billing/InvoiceHistory.cs:71–74` divides every currency by 100, and `Features/Billing/BillingHistoryView.cshtml:60` repeats that assumption in the invoice row. Summary money and invoice rows therefore share the same scale error.

**Local repro:** The three `InvoiceHistoryUsesTheCurrencyMinorUnit` cases provide customer-bound, provider-shaped invoice responses through the real Stripe.net parser and request the actual billing route. Assertions compare the rendered amount cell, so failures contain no complete HTML page, CSRF values, or customer data.

**Minimal proposal:** Add one Stripe-charge-unit formatter in `InvoiceHistory`, reuse it in `Money` and the invoice row, and retain integer/decimal arithmetic. This preserves USD precision and avoids a new dependency. See [proposed-source-fixes.patch](proposed-source-fixes.patch). Partial-paid invoice balances remain the checkout/UI worker's scope and are not changed here.

## Observed Free behavior; expectations require confirmation

### Completed Free setup blocks a different paid checkout for one hour

**Observed:** Complete local Free setup through `POST /billing/checkout` with `plan=free`, then request `plan=starter`. The latter returns **`422`** rather than the candidate test's expected **`303`**.

**Source:** `StripeBilling.cs:107` uses setup mode for the zero-priced plan; `:243–250` recognizes a completed setup intent and derives active payment validation. `:326–331` nevertheless treats every reservation less than one hour old as unfinished, unless a subscription has ended. A completed Free setup has no subscription. The public MCP contract at `Features/Billing/BillingMcpPresentation.cs:28` says to use `create_checkout` when no live subscription exists, but it does **not** promise immediate plan switching or specify completed-setup reservation timing.

**Classification:** Reproduced behavior and a contract/coverage question. No repository document or existing test found in the audit establishes the desired timing after Free setup. The failing `CompletedFreeSetupAllowsImmediatePaidCheckout` assertion represents the proposed expectation; it remains failing and is tagged `PaymentAuditExpectation`.

**Conditional proposal:** If completed Free setup should permit immediate paid checkout, allow replacement only when a different plan is requested from a validated, active, completed Free setup. Preserve same-plan retry identity and pending-checkout fencing. See [proposed-free-setup-policy.patch](proposed-free-setup-policy.patch); it is separate from verified-defect fixes.

### Free setup consumes the flag that suppresses paid subscription trials

**Observed:** A completed Free setup sets `TrialUsed=true`. After aging only that local reservation past the one-hour fence, Starter checkout omits `subscription_data[trial_period_days]`; the candidate test expects `30`.

**Source configuration:** `Features/Billing/PlanCatalog.cs:21–24` configures Free with zero trial days and each paid plan with 30. `StripeBilling.cs:115` sends those configured days only if `TrialUsed` is false. `:260` sets the flag for any active/trialing billing state, including Free setup. `BillingPlanPresentation.cs:46` and `BillingSummary.cshtml:35` likewise advertise a trial only while this flag is false.

**Classification:** The configured 30 days establishes the trial duration, **not** eligibility after Free setup. No explicit repository policy or existing test found says whether Free activation must consume or preserve that eligibility. `FreeSetupPreservesTheFirstPaidSubscriptionTrial` remains a failing proposed expectation, tagged `PaymentAuditExpectation`; it must not be filed as a confirmed pricing defect until that policy is settled.

**Conditional proposal:** If only a subscription consumes paid-trial eligibility, require a nonempty `subscriptionId` when setting `TrialUsed`. This preserves trial consumption for active/trialing subscriptions. See [proposed-free-setup-policy.patch](proposed-free-setup-policy.patch).

## Verified recovery and integration contracts

1. Both webhook aliases (`POST /api/v1/billing/stripe/webhook` and `POST /api/auth/stripe/webhook`) converge on current Stripe state across two local Kestrel replicas. Duplicate IDs and older event timestamps do not restore canceled paid entitlement. Failure → payment success → cancellation transitions use subscription facts, not invoice/event payload claims.
2. A deterministic held subscription read produces a real CAS conflict: the older active snapshot loses to a canceled projection, then rereads Stripe. Exactly three provider reads occur for the two deliveries, and the final organization/billing projections agree on Free. No timing sleeps or overlapping aggregate suites are used.
3. An injected I/O failure after the billing write but before the organization write returns `503`. Replaying the same event through the other alias rereads newer canceled state and repairs the projection. This proves retry convergence, **not** atomicity of the two records; they temporarily disagree after interruption.
4. Foreign currency, fractional price, and quantity-two subscription items clear paid-price qualification. The passing qualification test does not establish enforcement of resource capacity; the separate P1 failing test checks that downstream consequence. Restoring a valid USD price reactivates the binding.
5. Repeated pending checkout requests return the same session. Cancel expires the local provider session; a late invoice-paid event cannot establish a subscription or validate payment. Replacement uses a different checkout idempotency key and retains the configured unused paid trial. USD minor units remain exact.

The authoritative-state contract is documented at `docs/stripe.md:3–8`; retry-visible delivery failures are documented at `:79–81`. `StripeBilling.cs:167–192` verifies signatures/mode, uses the customer index, and invokes reconciliation; it does not keep an event-ID ledger. Duplicates intentionally re-read canonical state and advance the persisted revision. `:297–305` uses independent billing/organization conditional writes and checks the resulting billing revision. `Platform/Persistence/IDashboardState.cs` is the persistence integration boundary: `GetAsync` returns a version; `PutAsync` accepts absent/matching preconditions; conflicts must raise `TerraBasePreconditionFailedException`.

Fixed catalog database limits remain Free/Starter/Pro/Team = **1/5/25/100**. Monthly read allowances are **100K/1M/100M/50M**, as configured in `PlanCatalog.cs:57–60`; this audit does not invent monotonic tier limits or alter prices.

## New files and verification

Tests are confined to `tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/payment-webhook-correctness/`:

- `PaymentWebhookAuditJourneyTests.cs`: 11 HTTP/domain cases, including three verified-defect failures and two unresolved-expectation failures.
- `PaymentWebhookAuditScenario.cs`: existing DemoEnvironment integration, two local replicas, scoped protocol overrides, one interrupted-write fault, and a deterministic CAS barrier. It can forward only through the existing synthetic transport, never to a live payment service.
- `PaymentCurrencyEvidenceJourneyTests.cs`: desktop/mobile capture and invoice-table keyboard/scoped accessibility checks, using the pinned Playwright workflow documented at `docs/development.md:69–73`.

Commands (run from this checkout):

```sh
nice -n 19 dotnet build TerraScale.Dashboard.slnx --configuration Release -p:RestoreLockedMode=true
nice -n 19 make lint
nice -n 19 dotnet run --configuration Release --no-build --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/PaymentWebhookAuditJourneyTests/*'
nice -n 19 dotnet run --configuration Release --no-build --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/PaymentCurrencyEvidenceJourneyTests/*'
git apply --check docs/reviews/2026-10-13/payment-webhook-correctness/proposed-source-fixes.patch docs/reviews/2026-10-13/payment-webhook-correctness/proposed-free-setup-policy.patch
```

The locked Release build and full `make lint` pass. The domain suite deliberately exits **2**: **11 total, 6 passed, 5 failed, 0 skipped**. The failures remain enabled in normal discovery; no CI setting, project file, existing assertion, or test was changed. Raw domain results are in [evidence/payment-webhook-audit.trx](evidence/payment-webhook-audit.trx); [evidence/results.json](evidence/results.json) records classifications and final verification. Both patches pass applicability checks against the audited source; neither has been applied, compiled, or runtime-tested as a source fix.

The browser evidence suite passed **2/2**, with zero scoped Axe violations, at actual capture HEAD **`a4670e293fd7d3ccace131682697cf866166d717`**. The coordinator subsequently authorized committing small sanitized synthetic PNGs for this audit. Four byte-identical copies of those captures are committed under `evidence/screenshots/`, totaling **63,979 bytes (62.5 KiB)**. [The screenshot manifest](evidence/screenshots/manifest.json) records each image's SHA-256, size, viewport, section, audited source SHA and actual capture HEAD. No capture is attributed to a packaging commit that did not exist when it was taken.

| Viewport | Invoice table | Invoice summary |
| --- | --- | --- |
| Desktop, 1440×900 | [JPY 15.00 displayed for 1500 JPY](evidence/screenshots/currency-desktop.png) | [Incorrect JPY summary amounts](evidence/screenshots/currency-desktop-summary.png) |
| Mobile, 390×844 | [Keyboard-scrolled JPY invoice row](evidence/screenshots/currency-mobile.png) | [Incorrect JPY summary amounts](evidence/screenshots/currency-mobile-summary.png) |

All four images were visually inspected before packaging. The original section capture workflow excludes fixture identity areas; no emails, customer names, secrets, payment-method details or PNG text metadata are present. No additional masking was needed. The images retain clear defect evidence from the actual running local app. Original per-device metadata and capture outputs remain under ignored `evidence/test-results/`. No video was captured or committed.

The latest command prohibits browser sign-in, so the browser suite was **not rerun** for packaging. Its historical 2/2 result remains qualified by capture HEAD. The focused HTTP/domain suite, locked Release build, lint and patch applicability checks are rerun after the evidence commit; their exact final HEAD and results are retained locally in ignored `evidence/test-results/final-head/verification.json`. All failing domain assertions remain intact. The initial mobile capture attempt failed because a string-form Playwright wait was blocked by CSP; the existing new test uses the repository's function-form wait without changing or bypassing application CSP.

## Limits and ownership dependencies

- No live Stripe/auth/database call or credential retrieval was used. Test keys and identities are fixed existing synthetic fixtures. Provider documentation was read over public HTTPS without authentication. Local captures contain synthetic fixture data only; no real user/customer data is used.
- Full `fast`/`slow` aggregates, true database durability/replication, webhook signature age variations, exhausted CAS retries, actual browser-disconnect cancellation between projection writes, and Team-dunning cases were not run. Existing signature/body-size/outage cases in `Features/Billing/BillingWebhookJourneyTests.cs` were read, not rerun.
- The currency evidence covers its amount display, keyboard table focus/scroll, and scoped accessibility. Full checkout navigation, Back/Forward, repeated UI clicks, Close/Cancel, annual/monthly selection, and partial-paid invoice balances belong to the separate checkout/UI audit; they are not duplicated here.
- The verified patch touches `StripeBilling.cs`, `InvoiceHistory.cs`, and the amount cell on `BillingHistoryView.cshtml:60`. The conditional policy patch touches `StripeBilling.cs` only. Currency formatting and partial-balance work overlap the invoice row; combine them against current source rather than applying old row text blindly. Active shared R3–R6 refactoring may move these paths. No worker thread was read or contacted.
- Publication and any source fix remain separate actions. Publication stays held while Mongo has the active slot, until the coordinator explicitly grants this audit's serial slot. No push, PR, merge or auto-merge is authorized for this packaging step.
