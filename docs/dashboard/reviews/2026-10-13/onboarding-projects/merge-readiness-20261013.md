# PR #19 merge readiness after approved fixes

PR: https://github.com/TerraScale/ts-dashboard/pull/19

**Not ready to publish or merge:** the approved fixes pass their seven focused cases, but full Fast has three stale upstream assertions and full Slow has two. The precise next repair is [05-upstream-audit-contract-alignment.patch](05-upstream-audit-contract-alignment.patch). It applies cleanly but remains unapplied. A separate exception is required for these three existing audit test files; the prior approval covers only proposals 01–04 and the auto-merge setting.

## Applied approved changes

1. `OnboardingProjectProvisioner` validates the selected default project before renaming it. Interrupted setup with another selection now rejects before mutation.
2. `newProject=1` selects the Projects tab. Failed project creation reopens the dialog, preserves name/valid placement, and exposes its error inside the dialog; Cancel and edited retry work.
3. Organization/project switching keeps compatible local returns and maps an incompatible database-detail destination to `/databases`.
4. The billing audit journey uses an independent fixture for each test; the prohibited shared-session fixture and execution limiter are removed. All assertions remain active.

Product files:

```text
src/TerraScale.Dashboard/Features/Accounts/AccountEndpoints.cs
src/TerraScale.Dashboard/Features/Databases/OnboardingProjectProvisioner.cs
src/TerraScale.Dashboard/Features/Organizations/OrganizationEndpoints.cs
src/TerraScale.Dashboard/Features/Organizations/ProjectDialog.cshtml
src/TerraScale.Dashboard/Features/Organizations/ProjectDialogModel.cs
```

Approved test changes affect `BillingCheckoutAuditJourneyTests.cs` and the two onboarding audit journey files. The latter strengthen draft recovery and compatible return coverage. Report changes stay inside this workstream's exclusive directory. No existing account/member test or unrelated product code was changed.

## Exact ref state

- Original PR base: `95d16216a0750742261d19b0954a6ad9c9c44a12`.
- Current remote PR head: `f7af15aa60e64b535faddc6f07a56e2a91c13531`.
- Latest fetched/verified `origin/main`: `7b848fbf3596292dadb7d9d9cabe930f236eb6d4`.
- Cleanly rebased local audit parent: `56eba1a6b8fb171dc19611bef749a171df8ee1f9`.
- Approved fixes are committed after that parent; their exact SHA is recorded in the local capture manifest, final status and PR body. It cannot be embedded in its own committed report without changing the hash.
- Backup ref: `backup/audit-onboarding-projects-before-approved-fixes-20261013`.

No conflicts occurred. The remote remains the earlier audit-only head because `AGENTS.md` requires green Fast and Slow before pushing. Future publication must fetch/rebase again, stop on conflicts, and protect the existing remote branch against concurrent updates. No merge or force-push occurred.

## Validation

| Check | Result | Local ignored evidence |
| --- | --- | --- |
| Locked Release build, normal output paths | PASS; 0 warnings, 0 errors | `test-results/approved-fixes-normal-build.log` |
| Full lint | PASS | `test-results/approved-fixes-lint.log` |
| Security dependency checks | PASS; npm reports 0 vulnerabilities | `test-results/approved-fixes-security.log` |
| E2E boundary guard | PASS; 81 classes | included in lint log |
| Focused audit | PASS; 7 tests, 0 skipped | `test-results/approved-fixes/focused.trx` |
| Full Fast | FAIL; 325 passed, 3 failed, 0 skipped | `test-results/approved-fixes/fast-normal.trx` |
| Full Slow | FAIL; 88 passed, 2 failed, 0 skipped | `test-results/approved-fixes/slow.trx` |
| Diff whitespace check | PASS | `git diff --check` |

The earlier Fast run used redirected MSBuild artifacts and failed a fourth check because `StripeAuditJourneyTests` expects the production executable at `src/TerraScale.Dashboard/bin/Release/net10.0`. Running the normal build supplied that prerequisite. The normal Fast result above passed the executable test. No test assertion was weakened or excluded.

All local build, test and lint commands ran with `nice -n 19`. Full suites ran sequentially. No Alpha CI or additional spending was used.

## Remaining test-contract mismatches and proposed repair

### Role tests (four failed cases across Fast and Slow)

Routes: `GET /organization?tab=members`, `POST /api/v1/access/invitations?tab=invitations`.

`OrganizationOperations.AvailableRole` accepts `viewer`, `member`, and `admin` in the current main source. The HTTP choice test and browser choice test still require `viewer` to be absent. The repeated unsupported-role test submits the now-supported `viewer` role and expects 422; the valid invitation succeeds with 200 instead. These are stale assertions against a changed producer contract, not evidence that the approved project fixes changed authorization.

Proposal 05 requires Viewer to be present in HTTP and 1366px/320px browser forms. The repeated-negative test submits an explicitly unknown `unsupported-audit-role`, retains its 422 checks, checks the current safe error message, and keeps the existing denied-member, unchanged-identity and no-created-invitation assertions. No production role policy, fixture identity or security configuration changes are proposed.

Targets:

```text
tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/accounts-members-settings/AccountsMembersSettingsAuditJourneyTests.cs
tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/accounts-members-settings/AccountsMembersSettingsBrowserAuditJourneyTests.cs
```

### Billing retry test (one failed Fast case)

Route: `POST /billing/checkout` after the deterministic missing-index interruption.

The test submits no `Accept: text/html`, so the current `BillingEndpoints` intentionally returns a 503 problem JSON response. It then searches that JSON for the browser-only Try again anchor. `DatabaseEndpoints.WantsHtml` selects HTML only when the request accepts it, and `BillingCheckoutUnavailable.cshtml` already links Try again to `/organization?tab=billing`.

Proposal 05 sends `Accept: text/html` on this test's private client and additionally asserts `Content-Type: text/html`. It preserves the 503 and exact retry destination assertions. Other API checkout tests retain their original response negotiation. No billing source change is needed or proposed.

Target: `tests/TerraScale.Dashboard.E2E/PagePaymentAudit/Oct13/billing-checkout-subscriptions/BillingCheckoutAuditJourneyTests.cs`.

**Proposal status:** `git apply --check` passes against the current local source. Proposal 05 is not applied, compiled, or runtime-tested; passing outcomes for it are not claimed. After authorization, run the affected journeys and required checks on the combined head before publishing.

## Browser evidence and GitHub limits

The focused browser journey passes at 1440×900 and 390×844 with zero violations on sampled creation/onboarding WCAG AA scans. It tests keyboard, focus, loading, repeat submission, Cancel/Close/Escape, Back/Forward, creation, duplicate-name failure, preserved draft, retry and initial empty state. Current head provenance and hashes belong in the ignored `test-results/approved-fixes/browser/manifest.json`; captures are recaptured after the local fix commit. Historical captures are preserved separately and must not be presented as after-fix evidence.

Screenshots/videos remain local and ignored. The available GitHub browser profile returns 404 and Sign in for the private PR; no authenticated profile or supported attachment uploader is available. No tokens or credentials were retrieved. The PR explicitly records this attachment blocker. Media is not committed.

The approved REST PATCH to enable repository auto-merge returned success but still returned `allow_auto_merge:false`. REST and GraphQL rereads confirmed it stayed disabled. The repository is private on the Free organization plan; native auto-merge is unavailable without a plan change. No upgrade or permissions change occurred. Direct merging is already authorized once all readiness conditions pass.

## Overlap and unrun qualification

The five source files overlap shared Accounts/Organizations/provisioning code. Current ts-auth hydration and placement validation are preserved. No guided-import, banner, auth/security configuration, payment-source, replication or other-thread changes were made. Coordination is through this report only; no blocked external history was accessed.

No real user data or payments were used. Live production auth, persistent restart, full account-to-ts-typescript SDK qualification and deployment were not run. The local checks above do not substitute for those release-specific checks.
