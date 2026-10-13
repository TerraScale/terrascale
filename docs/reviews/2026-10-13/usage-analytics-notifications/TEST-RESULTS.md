# Focused local results

Audited production source: b719878083dffd9441e063bbfaccfa0dffa95abd.

New journeys: **3 passed / 7 expected failures**. Additional existing AnalyticsContractJourneyTests: **1 passed**. Combined focused execution: **4 passed / 7 failed, 11 total**. Each failing class command exited 2; the existing contract command exited 0. No failing assertion was disabled or weakened.

| Suite | Total | Passed | Failed | TRX finish time (UTC) |
|---|---:|---:|---:|---|
| New Analytics/Home | 6 | 1 | 5 | 2026-10-13T03:11:32.7116627Z |
| New Notifications/Audit/Usage | 4 | 2 | 2 | 2026-10-13T03:12:50.1612681Z |
| Existing Analytics wire contract | 1 | 1 | 0 | 2026-10-13T03:14:25.4585304Z |

Evidence-preparation rerun at local test/report head **afb0f3375a2800bc10fcdd5b942c3cd77fb077e9**: the table above records fresh focused results. New test assertions and production source are unchanged. Build again passed with 0 warnings/errors; scoped formatting, stylesheet, SDK provenance and E2E boundary checks passed. The final docs/PNG head is verified again in the handoff, with raw reports retained under obj/final-head-checks. This rerun does not relabel the original b719 screenshot capture source. No CI or publication was started while Mongo has the active slot.

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

From the repository root, the existing project compiles the four new files through its default C# item inclusion. Ignored local outputs were isolated under the unique test/report directories. The temporary targets file contained:

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
audit_results="$PWD/docs/reviews/2026-10-13/usage-analytics-notifications/obj/test-results"

nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/AnalyticsPageAuditJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename analytics.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/InboxAuditUsageJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename inbox-audit-usage.trx
nice -n 19 dotnet "$audit_dll" --treenode-filter '/*/*/AnalyticsContractJourneyTests/*' --report-trx --results-directory "$audit_results" --report-trx-filename existing-analytics-contract.trx

nice -n 19 dotnet format tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj \
  --include tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/AnalyticsPageAuditJourneyTests.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/AuditAnalyticsProducer.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/InboxAuditUsageJourneyTests.cs \
  tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/usage-analytics-notifications/PageAuditHost.cs \
  --verify-no-changes --no-restore --verbosity minimal

nice -n 19 npm --prefix src/TerraScale.Dashboard/web run check:css
nice -n 19 python3 scripts/verify-sdk.py
nice -n 19 python3 scripts/verify-e2e.py
```

Build: PASS, 0 warnings/errors. Scoped full dotnet format: PASS. CSS: PASS (95,800 committed bytes match). SDK provenance: PASS. TUnit production/test boundary: PASS (54 journey classes). These are focused checks, not a claim that make lint or the complete fast/slow release suites passed.

Each of proposed-source.patch, analytics-contract-proposals.patch and region-placement-policy.patch passed git apply --check. No proposal was applied, compiled or behavior-tested.

Raw TRX/logs and generated HTML may contain synthetic CSRF values and remain uncommitted under obj. This sanitized summary excludes them. The compact CSV evidence normalizes response CRLF line endings to LF without changing field values. Compact fixture JSON/CSV contains only synthetic route IDs, payloads and observed labels/paths. Analytics window timestamps are aligned relative to the captured clock; values and relative bucket spacing are deterministic. PageAuditHost freezes TimeProvider for each host pair to test exact audit boundaries without expired authentication cookies.

Not run: aggregate fast/slow, CI, live services/payments, exhaustive accessibility and delayed/stale browser responses. Draft publication is held while Mongo has the active serial CI slot; explicit authorization for this workstream's slot is still pending.

Test executable SHA-256: 0d2115fff1c0bc6ac7745f707c04332c8b7d1daf6cb9be98716d5cee63a794f4
