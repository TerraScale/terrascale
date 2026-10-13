# Billing checkout and subscription UI audit

Review [02-show-remaining-invoice-balance.patch](02-show-remaining-invoice-balance.patch) first: a partially paid invoice currently shows its original total as the outstanding balance.

Five defects are reproduced through the running dashboard. Eleven focused TUnit cases yield **5 passes and 6 expected failures** in 8.438 seconds at capture/test head `6a8a3b775420adcbb959d8ca2c0ef1cb58db1db0`, with no skipped assertions. The two annual-selector failures are the same defect at desktop and mobile sizes. Existing source is unchanged; patches below are proposals only.

## Verified defects

### 1. P2 — Partially paid invoice overstates the outstanding balance

**Route:** `GET /organization?tab=billing`, invoice summary.

**Source:** `src/TerraScale.Dashboard/Features/Billing/StripeBillingInvoices.cs:28` projects `invoice.AmountDue` and discards `invoice.AmountRemaining`. `src/TerraScale.Dashboard/Features/Billing/InvoiceHistory.cs:106` totals that original amount for open invoices.

**Runtime reproduction:** Authenticate a local fixture owner, create a fresh business organization, start an uncompleted local checkout, and install the owner-scoped `partial-payment` invoice scenario through `POST /__audit/billing/invoices`. The official Stripe SDK receives an open USD invoice with `amount_due=1234`, `amount_paid=1000`, and `amount_remaining=234`. Reload billing.

**Actual:** Outstanding balance is `$12.34`. **Expected:** `$2.34`. This is an inaccurate payment display; the audit does not establish an incorrect charge.

**Regression:** `PartiallyPaidInvoiceUsesRemainingAmountForOutstandingBalance` deliberately fails at the `$2.34` assertion. The invoice fixture is local, customer-scoped, and contains no customer payment information.

**Minimal proposed fix:** [02-show-remaining-invoice-balance.patch](02-show-remaining-invoice-balance.patch) carries Stripe's remaining amount through `BillingInvoice` and uses it only when summing outstanding balances. It preserves the original invoice total in the history table. A nullable final constructor argument preserves existing callers. The proposal also adds `amount_remaining` to the existing demo protocol response so its open invoices remain realistic; that fixture edit is not applied.

**If omitted:** Owners see a balance larger than their remaining debt and may misunderstand whether an earlier payment was credited.

### 2. P2 — Annual page opens the plan chooser with Monthly selected

**Route:** `GET /organization?tab=billing&interval=annual`, then **Choose plan**.

**Source:** `src/TerraScale.Dashboard/Features/Billing/BillingPlanDialogs.cshtml:6` initializes the chooser from subscription state alone. `BillingSummary.cshtml:8` independently initializes the visible plan cards from the query string.

**Runtime reproduction:** A fresh, unpaid business organization opens the annual billing page. The cards show Starter at `$50.00 billed annually`. Open the chooser without changing the interval.

**Actual:** Monthly is checked and Starter reads `US$ 5.00 / month`. **Expected:** Annual remains checked and Starter reads `US$ 50.00 / year`. The mismatch reproduces at **1440×900 and 390×844**. It can also affect a subscribed owner who explicitly selects an interval different from their current subscription.

**Regression:** Both arguments of `AnnualPlanPageKeepsAnnualSelectedWhenChooserOpens` deliberately fail on the checked-state assertion.

**Minimal proposed fix:** [01-preserve-annual-selection.patch](01-preserve-annual-selection.patch) uses a valid `monthly` or `annual` query value first, then falls back to the subscription interval. Invalid query values keep the existing fallback.

**If omitted:** The chooser changes the billing interval selected on the surrounding page. The later review does disclose the resulting interval, but the owner must notice and correct the reset.

### 3. P2 — Interrupted checkout's retry link leaves billing

**Route:** `POST /billing/checkout` after an interrupted checkout-index write.

**Source:** `src/TerraScale.Dashboard/Features/Billing/BillingEndpoints.cs:72` has no journey-specific dependency-failure response. The global handler at `DashboardApplication.cs:107` correctly returns HTTP 503, using `UI/Slices/ServiceUnavailable.cshtml:9`, whose **Try again** link points to `/`.

**Runtime reproduction:** Arm the existing owner-scoped `missing-index` scenario through `POST /__test/billing/checkouts/scenario`. Submit Starter monthly checkout. The Stripe session and primary binding persist, while the separate checkout-index write fails once.

**Actual:** HTTP 503 with **Try again → `/`**. **Expected:** HTTP 503 with a recovery link to `/organization?tab=billing`, where the owner can resume or cancel the pending checkout.

**Regression:** `InterruptedCheckoutOffersRetryBackToBilling` deliberately fails on the link target. Separately, `InterruptedCheckoutRetryRepairsIndexWithoutReplacingSession` passes: repeating the same checkout repairs the index and retains the original session. The defect is the recovery navigation, not lost payment state.

**Minimal proposed fix:** [03-keep-checkout-retry-in-billing.patch](03-keep-checkout-retry-in-billing.patch) catches dependency failures at this checkout endpoint and returns a small billing-owned unavailable view with the billing retry link. It preserves HTTP 503, CSRF/owner validation, reservation replay, and request-abort handling. It avoids modifying the shared error page owned by the active refactor.

**If omitted:** The offered retry action sends the owner to the dashboard instead of the interrupted billing journey.

### 4. P2 — Cancel checkout returns no visible outcome

**Routes:** `POST /billing/checkout/cancel` → `GET /organization?tab=billing&payment=canceled`.

**Source:** `src/TerraScale.Dashboard/Features/Billing/BillingPlanChanges.cs:167` returns `payment=canceled`. `BillingSummary.cshtml:20` does not render this outcome. `Organizations/OrganizationWizardContent.cshtml:4` has cancellation feedback, but that wizard is not open on the normal billing tab.

**Runtime reproduction:** Start a pending local checkout, open billing at 390×844, and activate **Cancel checkout** using Enter. The server redirects to the canceled return route; the cancel button disappears. Submit a different annual plan afterward.

**Actual:** No cancellation status message; the different plan is accepted. **Expected:** A visible, accessible **Checkout canceled** status confirms the return outcome and explains how to retry. Returning from Stripe's cancel URL can leave a resumable pending session, so the copy must also explain canceling that session before choosing a different plan.

**Regression:** `CancelCheckoutReturnsVisibleOutcomeAndCanStartDifferentPlan` verifies replacement succeeds, then deliberately fails because there are zero matching status messages.

**Minimal proposed fix:** [04-announce-canceled-checkout.patch](04-announce-canceled-checkout.patch) renders a `role="status"` message on the billing canceled-return route. It does not expire a session on a GET, claim that no charge ever occurred, or change billing state.

**If omitted:** Owners get no confirmation that cancellation completed and no explanation of the next billing action.

### 5. P2 — Canceled unpaid checkout still marks a paid tier Current

**Route:** `GET /organization?tab=billing&payment=canceled` after canceling an unpaid Starter checkout.

**Source:** `src/TerraScale.Dashboard/Features/Billing/BillingSummary.cshtml:32` marks a card current when its catalog ID and interval match the organization and binding. It does not require a subscription. The same template correctly shows **No plan** in the summary when billing is required and there is no subscription.

**Runtime reproduction:** Create a fresh fixture business organization, start Starter monthly checkout without completing it, cancel it through `POST /billing/checkout/cancel`, and load the returned URL. The mobile cancellation screenshot captures the result.

**Actual:** The summary says **No plan / Payment required**, while Starter has a **Current** badge and **Current plan** button. **Expected:** No paid card is labeled current until there is a corresponding subscription. The selected catalog ID alone is not evidence of an active paid plan.

**Regression:** `CanceledUnpaidCheckoutDoesNotMarkPaidTierAsCurrent` confirms the No plan summary, then deliberately fails because `ts-plan-current` is present.

**Minimal proposed fix:** [05-only-mark-subscribed-plan-current.patch](05-only-mark-subscribed-plan-current.patch) adds the existing `subscribed` predicate to the card's current-plan condition. This changes display only; it does not change organization state, grant entitlements, or touch Free/trial-state handling owned by the payment-domain audit.

**If omitted:** Owners see conflicting plan status immediately after canceling checkout.

## Passing regression coverage

1. **Concurrent/repeated checkout and cancel:** Two concurrent identical submissions return the same session URL. A different plan is refused while it is pending. Cancel replay returns the same outcome. A replacement annual plan receives a different session.
2. **Interrupted persistence recovery:** The existing missing-index fault returns 503. Retrying repairs the index without replacing the already bound Stripe session.
3. **Invoice failure recovery:** An invoice-provider outage renders the retry message rather than an empty-history claim. Restoring the local transport and applying a paid filter restores the 12-invoice result.
4. **Desktop keyboard journey:** Choose-plan Enter, Close, Escape, focus restoration, annual radio selection, review, Close review, Back, and Forward pass. The review exposes `US$ 50.00 / year`, the existing 30-day-trial wording, and tier limits. Axe WCAG 2.2 AA reports no violations; the document does not overflow horizontally.
5. **Mobile keyboard journey:** The same assertions pass at 390×844. Native radio activation uses Space because the segmented control intentionally places its visible label over the radio input.

The suite exercises real Kestrel routes, Razor rendering, sessions, CSRF, and the official Stripe SDK. It reuses `DemoEnvironment` and its persistent-index failure controls. The new invoice wrapper changes only local fixture invoice responses; it does not make live Stripe requests. All account, organization, and subscription changes happen only in the test environment.

The checkout lacks generated JavaScript. The audit's local host serves the exact committed Datastar input and standalone button-loading source through test-only routes, alongside the committed stylesheet. The normal asset build copies Datastar unchanged and bundles button-loading without imports. This allows browser verification without writing existing generated assets. It does not qualify the full production asset build.

## Verification and reproduction

Run from the bound worktree. Every local build, test, and lint command uses low CPU priority.

```sh
nice -n 19 dotnet build tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj \
  --configuration Release \
  --artifacts-path "$PWD/docs/reviews/2026-10-13/billing-checkout-subscriptions/artifacts" \
  -p:RestoreLockedMode=true \
  -p:DirectoryBuildTargetsPath="$PWD/docs/reviews/2026-10-13/billing-checkout-subscriptions/audit-build.targets"

nice -n 19 dotnet docs/reviews/2026-10-13/billing-checkout-subscriptions/artifacts/bin/TerraScale.Dashboard.E2E/release/TerraScale.Dashboard.E2E.dll \
  --treenode-filter '/*/*/BillingCheckoutAuditJourneyTests/*' \
  --report-trx \
  --results-directory "$PWD/docs/reviews/2026-10-13/billing-checkout-subscriptions/results"

nice -n 19 dotnet format tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj \
  --verify-no-changes --no-restore \
  --include tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/billing-checkout-subscriptions/BillingCheckoutAuditFixture.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/billing-checkout-subscriptions/BillingCheckoutAuditJourneyTests.cs

nice -n 19 python3 scripts/verify-e2e.py
nice -n 19 python3 scripts/verify-sdk.py

for patch in docs/reviews/2026-10-13/billing-checkout-subscriptions/*.patch; do
  git apply --check "$patch" || exit
done
```

**Pass:** Locked build, zero warnings/errors; focused format verification; TUnit E2E boundary guard; SDK provenance guard; patch applicability checks; five control cases above.

**Expected failure:** Six regression instances for the five defects. They assert the intended behavior and remain enabled. The focused test command exits nonzero. The source proposals have not been applied, compiled, or tested as fixes.

**Not run:** Aggregate fast/slow/release suites, full CSS/JS bundling and CSS equality lint, live billing/card flows, production auth/persistence, webhook correctness, and real cancellation or charges. The unchanged normal PR12 CI workflow is running; no paid service was added. Currency/minor-unit and Free setup/trial-state regressions are assigned to the separate payment-domain audit and have no duplicate cases here.

[runtime-results.json](runtime-results.json) records individual test outcomes and sanitized observed values. Ignored [tests.log](tests.log), [build.log](build.log), [format.log](format.log), and the [TRX result](results/TerraScale.Dashboard.E2E_net10.0_x64.trx) preserve the local run output.

## Browser evidence

All five images are actual running-app captures using synthetic local fixtures, captured and tested at audit head `6a8a3b775420adcbb959d8ca2c0ef1cb58db1db0`; the pinned product source is `b719878083dffd9441e063bbfaccfa0dffa95abd`. Every capture was inspected for secrets and personal data. The account footer and account controls are masked; visible organization names are synthetic. The five PNGs total 752,804 bytes (about 735 KiB). [Committed manifest](evidence/committed/manifest.json) records their actual capture head, timestamps and hashes. The latest user instruction explicitly authorizes committing these small screenshots through the existing repository access, overriding the attachment-only checkout guideline. The evidence-only publication commit changes no product or test source. Focused checks are rerun at its final head, with fresh captures retained locally; the PR body records that final head and results. Original capture provenance is retained rather than relabeled.

1. [Annual chooser, desktop](evidence/committed/annual-chooser-1440.png): annual page cards and Monthly-selected dialog, 1440×900.
2. [Annual chooser, mobile](evidence/committed/annual-chooser-390.png): the same mismatch, 390×844.
3. [Canceled checkout, mobile](evidence/committed/canceled-checkout-390.png): billing return without cancellation feedback, 390×844.
4. [Annual review, desktop](evidence/committed/keyboard-review-1440.png): disclosed annual price, limits, and keyboard focus, 1440×900.
5. [Annual review, mobile](evidence/committed/keyboard-review-390.png): the responsive review, 390×844.

The pixels of all five captures were inspected. There are no before/after images because source fixes are not applied. No video was captured. Collaborative preview attempts returned `Preview automation navigate timed out after 15000ms.` and `Preview automation navigate timed out after 10000ms.`; its snapshot also timed out. The temporary audit preview launcher was not responsive and was removed from the deliverable. The repository-documented Playwright journeys provided the browser evidence instead. No Tailscale preview readiness is claimed.

## Source and delivery boundaries

**Branch:** `audit/billing-checkout-subscriptions-20261013`, already bound by the orchestrator; no worktree was created or switched.

**Base and pinned audited product source:** `b719878083dffd9441e063bbfaccfa0dffa95abd`. That was `origin/main` when the audit started. Before publication, a normal fetch showed `origin/main` at `015b42c4825866131a00d48469650bcba04b2364`; the only changed path is the unrelated notification browser test. The audit keeps its original base because rebasing the checkout would change an existing shared file, contrary to the current source-read-only instruction. The PR diff must contain only authorized new audit files. The publication record supplies its exact final head SHA and repeated checks.

**New test files:** `tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/billing-checkout-subscriptions/BillingCheckoutAuditFixture.cs` and `BillingCheckoutAuditJourneyTests.cs`. Their location follows the user's exclusive-directory instruction; existing projects and fixtures were not edited.

**New report files:** this README, five proposal patches, `runtime-results.json`, `ci-capacity.json`, `audit-build.targets`, a report-local `.gitignore`, and five PNGs plus their manifest under `evidence/committed/`. The latest local `evidence-manifest.json`, fresh captures outside `evidence/committed/`, build outputs, logs, and TRX files stay under this report directory and are ignored. Current-head checks recapture the fixtures locally; the published manifest preserves the actual earlier capture head. See the PR validation record for the final tested publication head.

**Overlap dependencies:** The partial-balance proposal touches `InvoiceHistory.cs`, also used by the payment-domain worker's currency work. Its remaining-balance change is separate from currency formatting; integrate both without replacing either. It also proposes a one-line addition to the shared demo invoice response, which needs that fixture owner's integration. The other four proposals touch billing-owned UI/endpoints; retry recovery uses a billing-owned view to avoid the shared UI refactor. No other worker's files or thread history were read, and no worker was contacted.

**Publication authorization:** The latest authenticated user command authorizes this workstream's first serialized draft publication slot, permits normal Alpha CI, and preserves release-critical priority. Open/link publication is authorized once queue/capacity metadata permits it. Existing source, CI settings, and all regression assertions remain unchanged. There is no merge authorization.

**Host and capacity:** `gh repo view` confirms `https://github.com/TerraScale/ts-dashboard`; the official GitHub CLI reads repository/run/job metadata with existing credentials. [ci-capacity.json](ci-capacity.json) records the normal run queue across the organization's repositories. Repository runner inventory is empty; organization runner inventory returns HTTP 403 requiring organization runner permissions. No credential was retrieved or printed, and no scope/permission change was attempted. Active native job metadata is the available capacity evidence; release work retains priority.

**Earlier technical failure:** `fj pr search --repo TerraScale/ts-dashboard --state open 'billing checkout audit'` returned `Error: unexpected status code 410 Gone` from `src/prs.rs:1600:20`. The remote is GitHub. That legacy-host failure is separate from the publication denial; retired Forgejo is not retried. Any subsequently authorized PR must contain only these new deliverable files, disclose the expected failures, remain draft, and link the authorized committed screenshots by their relative repository paths. No merge or auto-merge is appropriate for this audit.

**Verdict:** Fix findings 1–5 before treating these billing journeys as complete. Start with the remaining-balance patch.
