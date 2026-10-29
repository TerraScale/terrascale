# Supply-chain audit — 2026-10-22

Base: `5f162f919050f9d8e933c7bf66d301c679be9cbb`.

`dotnet list TerraScale.Dashboard.slnx package --vulnerable --include-transitive --format json` audited both application/E2E projects with nuget.org advisory coverage: zero findings. `npm audit --omit=dev --json` in `src/TerraScale.Dashboard/web` also returned zero findings. High/critical before/after 0/0; lower 0. Dependency manifests and lockfiles are unchanged.

All four active root workflows declare read-only top-level permissions; all runners are GitHub hosted. Exceptions are explicit jobs: GHCR image publication requires `packages: write`; the separate Slow failure reporter uses `issues: write`. Contents/deployments/pull-requests/id-token/actions writes are absent. Read-only repository defaults remain appropriate.

All remote action references were verified as full upstream commit SHAs. No active PR-target trigger or untrusted free-form event-to-shell interpolation was found. Fork pull requests receive empty repository-secret values and downgraded job tokens.

Protected workflow defense proposals are retained as exact unified diffs in the workspace audit evidence, not applied here:

1. Restrict the issue reporter to push/main while retaining all PR Slow tests and artifacts. Today same-repository PR code can run in that writer job; fork tokens cannot acquire that write scope.
2. Explicitly verify `workflow_run.head_repository` before deploy admission. Existing checks already require successful push/main CI on the exact current main SHA and latest successful run; no PR-code execution was confirmed.
3. Restrict manual GHCR publication to main. This removes operator branch image publishing while ordinary CI image builds remain available.

Workflow ownership remains with the orchestrator lane. Raw scanner evidence, full writer analysis and `workflow-handoff.diff` are retained under workspace `.cos/security-20261022/evidence/ts-dashboard/`. Existing red deploy/Slow results are not resolved or reclassified by this audit. No runtime/deployment/product-version change or local full test run.
