# Onboarding and projects audit

Review `01-onboarding-selection-guard.patch` first: unfinished setup can rename the default provider project, return HTTP 503, and interrupt setup after mutating the wrong project.

Four verified defects; both the original and rebased seven-case runs report **3 PASS, 4 FAIL on those defects**. The four HTTP regressions still reproduce after rebasing onto `origin/main` at `7d0420ef1979b128fe212a23eb6c4227a2588859`. Release compilation and focused formatting pass. Source fixes remain proposals; the PR adds audit files only.

**Merge blockers:** four active desired-behavior regressions fail. Full lint stops because the checkout lacks the locked `browserslist` asset dependency; complete fast/slow suites have not run. GitHub reports `allow_auto_merge=false`. Repository settings, source fixes, and shared dependencies are outside this workstream's authorized scope.

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

## Regression coverage and checks

`tests/TerraScale.Dashboard.E2E` is the actual existing test project. This checkout is .NET 10 with RazorSlices and Datastar; the Go application has been removed. No checkout `.agents` directory or additional checkout `SKILL.md` files were present. The installed ponytail skill was read.

The new HTTP class contains six running-application journeys. Each test owns its fixture, as required by the current E2E boundary guard; no shared-session fixture or parallel-execution limiter remains:

1. Four desired-behavior regressions above: **FAIL**, each on the verified product behavior. Assertions are active.
2. `ProducerAuthenticatedSetupResumesAfterLoginAndValidationFailure`: **PASS** using the real `TsAuthGateway` against the existing signed local producer. It covers relogin into another replica, saved payment-step progress, canceled-payment copy, Free continuation, invalid region, retry, completion, and repeated final submission without renaming the project.
3. `RepeatedScopedSelectionRejectsForeignIdsAndExternalReturns`: **PASS** using the official management SDK against the existing local producer. It rejects another account's project with 404, preserves selection, rejects external and protocol-relative return URLs, and accepts repeated valid selection with a local return route.

Existing `WorkspaceRecoveryJourneyTests` already cover interrupted project metadata persistence, retry using the canonical provider identity, and revoked membership. Existing onboarding MCP journeys already cover paid/free setup across replicas. Those source paths were inspected; their tests were not duplicated or rerun as aggregate suites.

Release compilation uses the existing application/test projects and locked dependencies. The usual build target writes shared CSS/JS assets, so this audit uses an ignored, local `test-results/AuditBuild.targets` to omit that bundler only. All MSBuild outputs are redirected to this report's `test-results/artifacts`. The browser helper serves the committed button-loading source and integrity-pinned Datastar bytes directly; the browser holds its native creation request before sending it to capture real pending UI, then releases the unchanged request. Production code, configuration, fixture files, vendor files, assets, locks, and CI are unchanged.

Original browser/check outcomes and evidence labels are recorded in [validation.json](validation.json). Local raw TRX/logs live under the ignored `test-results` directory; they contain only synthetic fixture data but may include synthetic CSRF values in assertion markup, so they are not committed.

The original browser journey **passes** at desktop 1440×900 and mobile 390×844. It verifies keyboard opening and focus movement, Cancel/Close/Escape, restored focus, a single native create POST after two synchronous submissions, successful creation, Back/Forward, Free onboarding, and the initial empty database list. It records the missing-dialog and lost-form defects without hiding the four failing desired-behavior regressions. The native POST count in browser observations is two overall: one successful request despite repeat submission, and one later duplicate-name request.

WCAG 2/2.1/2.2 AA scans report **zero violations** on the project creation dialog and onboarding project step in both viewports. This is sampled automated coverage, not an accessibility certification of all pages. Pixel inspection confirms the mobile dialog stays within the viewport and scrolls vertically; the onboarding form and initial empty database state render at 390 pixels wide.

The T3 preview returned `Preview automation navigate timed out after 15000ms.` twice. Captures use the checkout's documented Playwright/Chromium workflow instead. Earlier capture-driver failures were corrected; only the final redacted captures below are delivery evidence. Emails are replaced with `[redacted email]`, synthetic account identity is concealed, and screenshots additionally mask identity fields. No source fix is represented as applied.

## Running-app evidence

Every capture below is labelled in `validation.json` with audited source commit **`b719878083dffd9441e063bbfaccfa0dffa95abd`**, its SHA-256, and the compiled test/harness hashes. These are local files in the ignored report directory, not committed assets or PR attachments. The two videos show navigation through creation, duplicate-name failure, registration, Free setup, and the initial empty database state.

1. **Desktop project flow:** [dialog](test-results/browser/desktop-dialog.png), [loading](test-results/browser/desktop-loading.png), [success](test-results/browser/desktop-success.png), [short video](test-results/browser/desktop-project-flow.webm).
2. **Mobile project flow:** [dialog](test-results/browser/mobile-dialog.png), [loading](test-results/browser/mobile-loading.png), [success](test-results/browser/mobile-success.png), [short video](test-results/browser/mobile-project-flow.webm).
3. **Verified broken recovery:** [desktop missing creation dialog](test-results/browser/desktop-sidebar-link-missing-dialog.png), [mobile missing creation dialog](test-results/browser/mobile-sidebar-link-missing-dialog.png), [desktop duplicate error](test-results/browser/desktop-duplicate-error-lost-form.png), [mobile duplicate error](test-results/browser/mobile-duplicate-error-lost-form.png).
4. **Initial setup:** [desktop first project](test-results/browser/desktop-first-project.png), [mobile first project](test-results/browser/mobile-first-project.png), [desktop empty databases](test-results/browser/desktop-initial-empty-databases.png), [mobile empty databases](test-results/browser/mobile-initial-empty-databases.png). Registration and optional-plan captures are listed in the manifest.

The fresh browser journey also passes at both viewports with zero violations in the sampled creation and onboarding forms. Fresh captures from the rebased audit are archived at `test-results/pr-preparation/browser`, keeping the original labelled evidence above intact. Final committed-head captures use `test-results/pr-head/browser`, with a separate ignored manifest and results linked locally from the PR description. They remain ignored local files. The PR changes no production UI, and no supported forge-attachment upload tool is available; local captures are not represented as PR attachments. The PR description records the final tested commit and any fresh-run outcome.

## Reproduce without modifying shared files

From the bound worktree, create the following ignored local file at `docs/reviews/2026-10-13/onboarding-projects/test-results/AuditBuild.targets`:

```xml
<Project><Target Name="BundleDashboardAssets" /></Project>
```

```sh
nice -n 19 dotnet build tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj \
  --configuration Release -p:RestoreLockedMode=true -p:UseArtifactsOutput=true \
  -p:ArtifactsPath="$PWD/docs/reviews/2026-10-13/onboarding-projects/test-results/artifacts" \
  -p:DirectoryBuildTargetsPath="$PWD/docs/reviews/2026-10-13/onboarding-projects/test-results/AuditBuild.targets"

nice -n 19 dotnet docs/reviews/2026-10-13/onboarding-projects/test-results/artifacts/bin/TerraScale.Dashboard.E2E/release/TerraScale.Dashboard.E2E.dll \
  --treenode-filter '/*/*/OnboardingProjectsAuditJourneyTests/*' --report-trx \
  --results-directory "$PWD/docs/reviews/2026-10-13/onboarding-projects/test-results"
```

Use the same executable with filter `/*/*/OnboardingProjectsAuditBrowserJourneyTests/*` for the focused browser journey. Compilation does not bundle assets; this command is an audit check, not release qualification.

## Source ownership, evidence, and delivery

**Original audited source HEAD:** `b719878083dffd9441e063bbfaccfa0dffa95abd`; the tracking ref at the original manifest capture was `015b42c4825866131a00d48469650bcba04b2364`. **Publication base:** `95d16216a0750742261d19b0954a6ad9c9c44a12`, fetched and rebased cleanly. **Bound branch:** `audit/onboarding-projects-20261013`. The orchestrator supplied this worktree; no worktree was created or switched. The PR description records the published audit head.

Three proposed patch files pass `git apply --check` against the publication base. The guard and return-navigation proposal contexts were updated for the canonical ts-auth refactor; the proposals remain unapplied. They have not been compiled or runtime-tested. Their target files overlap the active source/refactor work, so the source owner must adapt them after R3–R6 settles and run these desired-behavior regressions on the combined head.

1. The onboarding guard belongs in `Features/Databases/OnboardingProjectProvisioner.cs`; it is a canonical project-selection check, independent of guided import behavior. No import changes are proposed.
2. Creation/retry changes belong in `Features/Organizations`. Preserve current placement/auth checks when reconciling with R3–R6.
3. Return navigation belongs in `Features/Accounts/AccountEndpoints.cs`. It uses the existing context and redirect sanitizer; it does not alter auth/security configuration. Database detail owners should verify navigation after integration.

No other threads or blocked histories were read or contacted. Banner removal is outside scope. Only local synthetic providers were used; no real user data, real payments, deployments, Alpha CI, paid services, credential retrieval, or account/security changes occurred.

The user requested an audit PR after reviewing the findings; it is prepared as a draft. It must remain draft while the four active regressions fail; full fast/slow gates remain unqualified. Full `make lint` was attempted on the rechecked source base `7d0420ef` and stopped at `ERR_MODULE_NOT_FOUND` for `browserslist`; no shared dependencies or assets were changed to work around it. Focused formatting, SDK/E2E boundary guards, and locked Release compilation pass. Proposed source patches remain uncompiled and runtime-untested. Live authentication, persistent restart and account-to-ts-typescript SDK qualification, and real payments were not run. No merge occurred. GitHub disables repository auto-merge, and this workstream does not authorize changing that setting. Original screenshots are historical audit evidence, not after-fix captures; the PR description distinguishes fresh tested-head results.
