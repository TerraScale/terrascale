# Focused local results

## Current publication verification

Rebased main/source base: **95d16216a0750742261d19b0954a6ad9c9c44a12**. Focused current-source verification: **9 passed / 5 retained contract-policy failures, 14 total**. HTTP: **6 passed / 5 failed**; browser: **3 passed / 0 failed**. AnalyticsPageAuditJourneyTests remains 1 passed / 5 failed. InboxAuditUsageJourneyTests is now **4 passed / 0 failed**, because main includes the audit-severity and Usage-navigation fixes. Existing AnalyticsContractJourneyTests and Reporting browser case each pass; both synthetic notification viewport cases pass. All five policy assertions and both defect regression assertions remain strict.

The three new test classes now use per-test fixtures to match current main's lint boundary. No session sharing/parallel limiter remains; Analytics fixture evidence writes to a database-specific directory. Locked Release build (0 warnings/errors), scoped full format, CSS (95,800 bytes), SDK provenance, Slow-report self-check and document-change checks pass. Full make lint passed on base 7d0420ef (76 classes). After refreshing to base 95d16216, make lint/verify-e2e fails at scripts/verify-e2e.py:40 because the newly merged, pre-existing tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/billing-checkout-subscriptions/BillingCheckoutAuditJourneyTests.cs has NotInParallel. This unrelated baseline file is read-only and unchanged by this PR. Suites ran one at a time. Raw current reports are under obj/publication-checks; the exact final PR head/results are supplied in the handoff. Aggregate Fast/Slow and live services remain unrun; the five known-red policy cases are included in Fast and will keep its gate red. No CI configuration or test exclusion was introduced.

The historical presentation patch no longer applies to current main (the fixes already landed). Both conditional Analytics patch files still pass git apply --check; no patch was applied/compiled. Native GitHub auto-merge is disabled (allow_auto_merge=false); publication is draft-only while the five policy cases are red. The user's publication request supersedes the earlier Mongo-slot hold; repository settings and merge requirements remain intact.

## Historical pre-rebase verification

Audited production source: b719878083dffd9441e063bbfaccfa0dffa95abd.

Final focused coverage: **7 passed / 7 expected failures, 14 total**. HTTP: **4 passed / 7 expected failures, 11 total**. Browser: **3 passed / 0 failed**, covering an existing Reporting case and two new synthetic notification viewport cases. New journeys contribute 5 passes / 7 expected failures; existing journeys contribute 2 passes. The two failing HTTP class commands exit 2; passing class commands exit 0. No established failing defect/contract assertion was disabled or weakened.

| Suite | Total | Passed | Failed | TRX finish time (UTC) |
|---|---:|---:|---:|---|
| New Analytics/Home | 6 | 1 | 5 | 2026-10-13T03:11:32.7116627Z |
| New Notifications/Audit/Usage | 4 | 2 | 2 | 2026-10-13T03:12:50.1612681Z |
| Existing Analytics wire contract | 1 | 1 | 0 | 2026-10-13T03:14:25.4585304Z |

Historical evidence-preparation rerun at **afb0f3375a2800bc10fcdd5b942c3cd77fb077e9**: the table above records that execution. The HTTP cases and existing Reporting browser case were rerun at **30d4e6665681be00e7c6130b1ba5beca0caf948f** with the same HTTP counts and one additional browser pass. A new synthetic-only notification browser file adds two passes. The final deliverable commit and post-commit verification are supplied in the handoff; raw reports are retained under obj/final-head-checks. Production source and the seven established failing assertions remain unchanged. These checks do not relabel the original b719 screenshot capture source. No CI or publication was started while Mongo has the active slot.

| Final focused suite | Total | Passed | Expected failures |
|---|---:|---:|---:|
| AnalyticsPageAuditJourneyTests | 6 | 1 | 5 contract/policy proposals |
| InboxAuditUsageJourneyTests | 4 | 2 | 2 presentation regressions |
| AnalyticsContractJourneyTests | 1 | 1 | 0 |
| ReportingJourneyTests.NativeAnalyticsUsageSpendingAndSettingsDesktopAndMobile | 1 | 1 | 0 |
| SyntheticNotificationsBrowserJourneyTests | 2 | 2 | 0 |

The Reporting case uses newly enrolled example.test accounts, local Kestrel replicas and in-process auth/reporting/Stripe transports (DemoHost.cs:82–105, :663–681; TestReporting.cs:65–71). Shell scripts/fonts/styles load from local assets. The new notification case uses only PageAuditHost's fresh synthetic records and fixed test account; a browser route guard aborts every request outside its local host. No real login/payment/provider request is involved. The existing ReferenceScenario notification browser case was inspected, not executed: its captured persona fails the synthetic-only prerequisite. No credentials were retrieved.

## Notification opening-time validation

The first new browser execution passed desktop but failed the mobile zero-violation scan with color-contrast entries while the dialog was opening. Diagnostic runs recorded ts-dialog animations in state running with opacity 0 on desktop and 0.650245/0.370364 on mobile, with meta color rgb(133, 142, 163). Current CSS defines a 180 ms opacity transition from zero to one (web/assets/css/overlays.css:163–164, base.css:190–193, tokens.css). The settled scans pass on both viewports.

The new test waits for document.fonts.ready and the dialog's actual animation.finished promises before its strict WCAG 2.2 AA scan; it uses no fixed sleep or disabled rule. This corrects the measurement boundary for this new case. It does not change the existing notification SSE tests or any of the seven failing defect/contract regressions. Opening observations are retained locally under test-results/dotnet/oct13-synthetic-notifications/dialog-opening-*.json and dialog-presentation-*.json. No steady-state contrast bug is filed from the initial opening scan.

Notification browser checks pass for unread page-2 rendering, native POST/303 and status/page retention, read-all page clamp/empty/disabled action, scope exclusion in the dialog, Enter activation, Close and Escape focus return, unchanged page URL, overflow and zero browser errors/foreign origins. Reporting checks pass for Analytics periods, database/region scope, empty/error/null/zero states, Home/End keyboard movement, Usage rendering and desktop/mobile WCAG scans. The test stops on its first failing assertion, so an unsuccessful run is never counted as covering later steps.

## Failure classification

| Cases | Result and interpretation |
|---|---|
| Duplicate row on Home and Analytics (2) | Expected failure of proposed cross-item uniqueness policy; single-row 10 requests/4 KiB baseline passed, repeated row renders 20/8 KiB. Not a final bug label. |
| SFO sample for current GRU-only database on Home and Analytics (2) | Expected failure of proposed current-placement policy; historical placement contract unresolved. |
| Omitted middle latency minute (1) | Expected failure of missing-bucket proposal; explicit-null baseline passed with three columns and separate paths. Omitted minute renders two continuous columns. |
| Missing audit severity (1) | Concrete regression: null becomes CSV info, info-filter count=1, informational UI count=1 while the page reports missing severity. |
| Usage tab filter retention (1) | Concrete regression: settings/overview hrefs omit range/database; roundtrip restores 7d and All databases. |

The failing CSV, Usage href and sparse-label assertions stop their individual cases; subsequent strict assertions remain in the tests but are not counted as passing. Fixture evidence is collected before those first failures.

## Exact commands

From the repository root, the existing project compiles the five new files through its default C# item inclusion. Ignored build/TRX outputs were isolated under the unique test/report directories; existing browser helpers use ignored test-results/dotnet. The temporary targets file contained:

```xml
<Project><Target Name="BundleDashboardAssets"><Message Importance="high" Text="Audit build: preserve existing browser assets." /></Target></Project>
```

This local build override preserves committed assets. It is not a tracked configuration/CI change. Stylesheet verification separately checked the existing committed CSS.

```bash
nice -n 19 dotnet build tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj --configuration Release \
  -p:RestoreLockedMode=true \
  -p:ArtifactsPath="$PWD/tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/obj/build" \
  -p:CustomAfterMicrosoftCommonTargets="$PWD/tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/obj/PreserveSource.targets"

audit_dll=tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/obj/build/bin/TerraScale.Dashboard.E2E/release/TerraScale.Dashboard.E2E.dll
audit_results="$PWD/docs/reviews/2026-10-13/usage-analytics-notifications/obj/final-head-checks"

nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/AnalyticsPageAuditJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename analytics.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/InboxAuditUsageJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename inbox-audit-usage.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/AnalyticsContractJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename existing-analytics-contract.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/ReportingJourneyTests/NativeAnalyticsUsageSpendingAndSettingsDesktopAndMobile' --report-trx --results-directory "$audit_results" --report-trx-filename reporting-browser.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/SyntheticNotificationsBrowserJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename synthetic-notifications-browser.trx

nice -n 19 dotnet format tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj \
  --include tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/AnalyticsPageAuditJourneyTests.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/AuditAnalyticsProducer.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/InboxAuditUsageJourneyTests.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/PageAuditHost.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/SyntheticNotificationsBrowserJourneyTests.cs \
  --verify-no-changes --no-restore --verbosity minimal

nice -n 19 npm --prefix src/TerraScale.Dashboard/web run check:css
nice -n 19 python3 scripts/verify-sdk.py
nice -n 19 python3 scripts/verify-e2e.py
```

Build: PASS, 0 warnings/errors. Scoped full dotnet format: PASS. CSS: PASS (95,800 committed bytes match). SDK provenance: PASS. TUnit production/test boundary: PASS (55 journey classes). These are focused checks, not a claim that make lint or the complete fast/slow release suites passed.

Each of proposed-source.patch, analytics-contract-proposals.patch and region-placement-policy.patch passed git apply --check. No proposal was applied, compiled or behavior-tested.

Raw TRX/logs and generated HTML may contain synthetic CSRF values and remain uncommitted under obj. This sanitized summary excludes them. The compact CSV evidence normalizes response CRLF line endings to LF without changing field values. Compact fixture JSON/CSV contains only synthetic route IDs, payloads and observed labels/paths. Analytics window timestamps are aligned relative to the captured clock; values and relative bucket spacing are deterministic. PageAuditHost freezes TimeProvider for each host pair to test exact audit boundaries without expired authentication cookies.

Not run: aggregate fast/slow, CI, live services/payments, exhaustive accessibility and delayed/stale browser responses. Draft publication is held while Mongo has the active serial CI slot; explicit authorization for this workstream's slot is still pending.

Expanded test executable SHA-256 before the final report commit: 7ae8ead66fce5dccf5592bd2d475dcd17f48010dafe9a80b0e39e5efcd2f6ff9. Historical executable before the synthetic browser file: 0d2115fff1c0bc6ac7745f707c04332c8b7d1daf6cb9be98716d5cee63a794f4. Builds include commit metadata, so post-commit executable hashes differ even when source/test content is unchanged. The exact final-head executable/capture hashes are retained locally in obj/final-head-checks/verification-manifest.json and are not substituted for older provenance.
