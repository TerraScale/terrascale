# Supply-chain remediation — 2026-10-22

Base: `e7fa366ddb289f5612c93d316b316fb92c568151`.

The Go benchmark used Go 1.25.0. Native `govulncheck ./...` found 35 unique reachable standard-library advisories. Raising its minimum to Go 1.27.3 removes those findings; the after scan records Go 1.27.3. The Go database does not supply numeric severity, so before high/critical counts are unclassified, not zero.

`cargo audit --json` found six advisories in the Rust benchmark lock: four in rustls, one in rustls-webpki and one in webpki-roots. Targeted patches 0.23.46 / 0.103.16 / 1.0.10 reduce the audit to zero advisories at every severity. Four supplied CVSS vectors are below high; two certificate-trust advisories have no supplied CVSS severity. No unrelated dependency resolution changed.

`dotnet list <project-or-solution> package --vulnerable --include-transitive --format json` audited all 31 tracked C# projects, including nested benchmarks and the build-check project. Initial missing-assets/SDK-lock failures are retained; explicit restore and the official pinned-SDK wrapper completed coverage. No NuGet vulnerabilities were reported. `npm audit --omit=dev --json` in `aspire` returned zero findings. No clean claim is inferred from a failed scanner.

All active workflows declare read-only top-level permissions and use GitHub-hosted runners; upstream commit verification covered every remote action. Explicit writer jobs: Slow `issues: write`, preview `pull-requests: write`, live E2E `id-token: write`, GHCR `packages: write`. No contents/deployments/actions writes are required. Read-only repository defaults remain appropriate.

Forks are excluded from SDK-key/preview/Slow credential jobs and cannot qualify Fast until a trusted branch runs them. No fork-secret exposure or active PR-target/event-injection path was found. Protected defense proposals pin preview cleanup scripts to main (same-repo authors are otherwise trusted) and restrict manual GHCR publication to main. Those exact diffs remain with the workflow owner.

Raw before/after scanner logs, exact commands and toolchain metadata, all lower findings, action-pin verification and `workflow-handoff.diff` are retained in workspace `.cos/security-20261022/evidence/terrascale/`. CI is the test gate; no local full suites, StorageFormat/API/product-version change, deployment or activation occurred.
