# Onboarding and projects audit

The three approved product patches now fix all four verified onboarding/project defects. The seven focused cases pass, including desktop/mobile creation, draft recovery, retry, keyboard controls, history, accessibility scans, and producer-authenticated interrupted setup.

**PR #19 is not ready to merge.** The properly built full Fast suite has **325 PASS, 3 FAIL, 0 SKIP**. Those three failures are upstream audit tests with stale role and response-format assumptions. Their precise, unapplied repair is [05-upstream-audit-contract-alignment.patch](05-upstream-audit-contract-alignment.patch). The approved fixes have not been pushed because checkout instructions require green Fast and Slow tests before pushing.

## Current approved change and validation

The user approved applying proposals 01–03, fixing the billing audit's fixture isolation, and attempting GitHub auto-merge without a paid upgrade. Five product files implement the proposals; the billing journey now owns a fixture per test. No assertions were removed. The onboarding browser regression now requires the sidebar dialog, retained duplicate-name draft, Cancel, and an edited retry to succeed. The selection regression also verifies a compatible database-detail return when switching back.

The original audit findings and historical evidence remain below. References there to observed failures describe the pre-fix source. The applied patches are [01](01-onboarding-selection-guard.patch), [02](02-project-dialog-navigation-and-retry.patch), [03](03-selection-return-navigation.patch), and [04](04-upstream-billing-test-isolation.patch). Proposal 05 remains unapplied and needs a separate exception for three existing audit test files; it does not change authentication or authorization policy.

| Local check | Result | Evidence |
| --- | --- | --- |
| Normal locked Release build | PASS, zero warnings/errors | `test-results/approved-fixes-normal-build.log` |
| Full `make lint` | PASS | `test-results/approved-fixes-lint.log` |
| `make security` | PASS, zero reported npm vulnerabilities | `test-results/approved-fixes-security.log` |
| Focused HTTP/browser audit | PASS, 7 cases | `test-results/approved-fixes/focused.trx` |
| Full Fast, normal build paths | FAIL, 325 passed / 3 failed / 0 skipped | `test-results/approved-fixes/fast-normal.trx` |
| Full Slow | FAIL, 88 passed / 2 failed / 0 skipped | `test-results/approved-fixes/slow.trx` |

The first Fast run used redirected build outputs and also failed the executable-path prerequisite. A normal `make build` corrected that prerequisite; the properly built run above leaves only the three stale upstream assertions. No product fix, test exclusion, or disabled assertion was used to remove that execution failure.

GitHub accepted the approved repository PATCH but still reports `allow_auto_merge=false`. Rereads confirmed the setting stayed disabled. Native auto-merge is unavailable for this private repository on the current Free plan. No paid upgrade or permissions change occurred. Direct merging remains authorized after readiness is established.

## Verified defects, in priority order

### 1. P1 — switching projects during unfinished setup partially renames another project

**Routes:** `POST /onboarding`, `POST /login/code/verify`, `POST /onboarding/step`, `POST /api/v1/dashboard/organization/projects`, `POST /api/v1/dashboard/switch-project`.

1. Register and verify a synthetic local account. Save account information and continue on Free; leave the project step unfinished.
2. Visit `/organization?tab=projects`, create another project in GRU, and select it.
3. Submit the unfinished project step with a new name and GRU.

**Observed at original source `b7198780`:** HTTP 503. The authenticated provider's default project name becomes `Must not rename the default`, while `/api/v1/dashboard/context` still reports its name as `Personal`. The account remains unfinished. The selected project was verified before submission, and the external protocol fixture reported no contract failures.

**Recheck at `7d0420ef`:** the provider default is still renamed before HTTP 503. The new canonical ts-auth hydration refreshes the dashboard name on a subsequent context read, so the original local/provider name mismatch no longer persists. The unwanted mutation and interrupted setup remain.

**Cause in current source:** `Features/Databases/OnboardingProjectProvisioner.cs:19` resolves the default project and renames it at line 21. `Features/Onboarding/OnboardingService.cs:66` calls this before checking at line 67 that the returned ID matches the selected project. That check throws after the provider mutation. Global dependency handling returns 503.

**Expected:** validate the selected canonical default project before any provider rename. Return a setup validation message when another project is selected; keep both projects unchanged.

**Proposed fix:** [01-onboarding-selection-guard.patch](01-onboarding-selection-guard.patch), a guard in the provisioner immediately after resolving the default project. No authentication or authorization configuration changes. The user can return to their Personal default selection through the existing organization/project controls.

**Regression:** `InterruptedOnboardingRejectsAnotherSelectionBeforeRenamingTheDefaultProject` requires both provider and dashboard names to match the original name and records HTTP 503 in its assertion. Comparing against the original name also catches unwanted mutation when a later context read refreshes dashboard metadata. It requires rejection before mutation, a validation redirect, and incomplete setup afterward.

### 2. P2 — the sidebar's Create new project link omits the creation dialog

**Route:** `GET /organization?newProject=1#projects`.

1. Sign in and open the organization/project selector in the sidebar.
2. Choose **Create new project**.

**Observed:** HTTP 200 showing the Organization tab, with no `#create-project` dialog. The fragment points to a projects section that this tab does not render.

**Cause:** `UI/Slices/ShellSidebar.cshtml:31` links to the route above. `Features/Organizations/OrganizationEndpoints.cs:116` defaults the absent `tab` to `organization`; it separately sets `OpenCreateProject`. `OrganizationView.cshtml:42` renders the dialog only on `tab=projects`.

**Expected:** the link renders and opens the creation dialog in the Projects tab.

**Proposed fix:** the `PageModelAsync` change in [02-project-dialog-navigation-and-retry.patch](02-project-dialog-navigation-and-retry.patch) selects the Projects tab whenever `newProject=1`, covering existing links and direct navigation.

**Regression:** `SidebarCreateProjectLinkRendersTheRequestedDialog` follows the actual rendered sidebar anchor and fails because the response has no creation dialog.

### 3. P2 — a rejected creation submission loses the form and entered values

**Route:** `POST /api/v1/dashboard/organization/projects` from `/organization?tab=projects`.

1. Open the creation dialog and enter `Recover this draft`.
2. Submit an unsupported region (`ZZZ`) using a native form request. A duplicate valid project name exercises the same recovery path through the browser.

**Observed:** HTTP 422 with `Choose serving project regions.`, but the response shows the Organization tab and omits the creation dialog and draft. No provider project was created. Duplicate-name submissions return the same missing-form state with their conflict message.

**Cause:** `ProjectDialog.cshtml:2` submits to a URL without the tab query. `OrganizationEndpoints.cs:221` uses `PageAsync`, which builds the page from the POST URL. Neither tab/create-dialog state nor draft fields are carried into the response. `ProjectDialogModel.cs` has no submitted-value fields.

**Expected:** show the error inside an open, editable creation dialog, preserve the name and valid placement choices, and allow retry or Cancel. Preserve the validation status and do not create a project.

**Proposed fix:** the remaining changes in [02-project-dialog-navigation-and-retry.patch](02-project-dialog-navigation-and-retry.patch) retain the creation form's draft on `AccountAccessException`, select Projects, reopen the dialog, and expose its error inside the modal. Region selections are limited to the serving catalog; Razor escapes submitted names. This proposal covers validation/conflict recovery, not a generic redesign of 503 handling.

**Regression:** `InvalidProjectPlacementKeepsTheCreateFormAndDraft` verifies HTTP 422, the validation message, and unchanged provider create count before failing on the absent dialog. It also requires the retained name and open state.

### 4. P2 — project switching returns to another project's database URL

**Routes:** `POST /api/v1/dashboard/switch-project`, then `GET /databases/{databaseId}?created=1`.

1. Complete setup and create a database in the initial project. Its detail page returns HTTP 200.
2. Create an empty second project.
3. Switch to it with the database detail route as `returnUrl`, as the shell does for the current page.

**Observed:** switching succeeds with HTTP 303 and saves the second selection, but its Location remains the first project's database detail URL. Following it returns HTTP 404.

**Cause:** `Features/Accounts/AccountEndpoints.cs:45` sanitizes external URLs but does not check whether the destination database belongs to the newly selected project. `ShellModel.cs:10` passes the current route through to the switch form.

**Expected:** keep a compatible local route; return to `/databases` when the old database ID is outside the new selection. Never expose another project's database.

**Proposed fix:** [03-selection-return-navigation.patch](03-selection-return-navigation.patch) rereads the selected context after either organization or project switching and maps an incompatible `/databases/{id}/...` destination to `/databases`. The existing local-redirect sanitizer still runs first. The organization variant is proposed because both endpoints share the same return behavior; the database-detail repro exercised the project variant.

**Regression:** `SwitchingProjectFromDatabaseDetailsReturnsToTheDatabaseList` fails on the destination's HTTP 404. It requires `/databases`, the saved new selection, and the empty database list.

## Regression coverage and reproducibility

`tests/TerraScale.Dashboard.E2E` is the existing test project. The current checkout uses .NET 10, RazorSlices, and Datastar. No checkout `.agents` directory or additional checkout skill files were present; the installed ponytail skill applies.

Six HTTP journeys use independent fixtures and local deterministic ts-auth/database producers. They cover the four repaired defects, relogin across replicas with unfinished setup, invalid placement and retry, Free continuation, repeat final submission, foreign project rejection, safe local redirects, and repeated valid selection. Existing workspace recovery and onboarding MCP journeys are exercised by the complete suites.

The browser journey uses the checkout's documented Playwright workflow after two T3 preview navigation timeouts: `Preview automation navigate timed out after 15000ms.` It covers 1440×900 desktop and 390×844 mobile, keyboard opening, focus containment and restoration, Cancel/Close/Escape, repeat submission with one native create POST, success, Back/Forward, the sidebar creation link, duplicate-name failure with a retained draft, edited retry, Free onboarding, and the initial empty database list. Three POSTs occur overall: initial successful creation, duplicate rejection, and successful edited retry.

The sampled creation and onboarding forms report zero WCAG 2/2.1/2.2 AA violations in both viewports. This is sampled automated coverage. The narrow dialog fits the viewport and scrolls vertically. Recordings and screenshots conceal synthetic identity and redact emails.

```sh
nice -n 19 make build
nice -n 19 make lint
nice -n 19 make security
nice -n 19 make test-fast
nice -n 19 make test-slow
```

Run the focused audit from the already-built executable with:

```sh
nice -n 19 dotnet tests/TerraScale.Dashboard.E2E/bin/Release/net10.0/TerraScale.Dashboard.E2E.dll \
  --treenode-filter '/*/*/OnboardingProjectsAudit*/*' --report-trx \
  --results-directory "$PWD/docs/reviews/2026-10-13/onboarding-projects/test-results/approved-fixes"
```

Normal bundling now runs with the installed, locked npm dependencies. Tracked generated CSS/JS, configuration, locks, vendor files, project files and CI were not changed. The initial audit's custom output/bundler override was used only for historical audit checks; it is not release qualification. Raw logs/TRX and captures remain ignored because test responses may contain synthetic CSRF markup.

## Running-app evidence and delivery limits

Historical audit evidence is labelled in [validation.json](validation.json) with source SHA `b719878083dffd9441e063bbfaccfa0dffa95abd`. Earlier captures remain under `test-results/browser`, `test-results/pr-preparation/browser`, and `test-results/pr-head/browser`; they are before-fix history, not current verification.

Fresh after-fix captures use `test-results/approved-fixes/browser`: desktop/mobile dialog, loading, success, `sidebar-link-create-dialog`, `duplicate-error-retained-form`, `retry-success`, first-project and initial-empty-databases screenshots, plus desktop/mobile project-flow videos. Exact committed-head provenance is recorded in the readiness report and ignored capture manifest when recaptured. An uncommitted capture is not labelled as a tested PR commit.

GitHub's private PR returns 404/Sign in in the available browser profile. No authenticated profile or supported attachment upload path is available, and no credentials were retrieved. Media remains local and ignored; it is not represented as uploaded PR evidence. The PR must state this attachment limitation.

## Source ownership and scope

The bound worktree was supplied by the orchestrator; no worktree was created or switched. The approved source fixes were rebased cleanly onto `origin/main` at `7b848fbf3596292dadb7d9d9cabe930f236eb6d4`. The readiness report records original/remote/local SHAs and the exact changed files.

The guard overlaps the database provisioner; creation/retry overlaps Organizations; return navigation overlaps Accounts. They preserve current placement/auth checks and use canonical ts-auth context. No guided import, banner, replication, auth/security configuration or unrelated payment source changes were made. Further cross-slice proposals are coordinated only through this report; no other threads or blocked histories were read or contacted.

Only deterministic local synthetic providers were used. Live authentication, persistent restart, account-to-ts-typescript SDK qualification, and real payments were not run. No deployment, Alpha CI, paid service, new spending, token retrieval, account/security change, or merge occurred.
