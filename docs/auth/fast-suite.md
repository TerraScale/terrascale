# Auth Fast coverage and runtime

Auth Fast runs only the happy path and critical paths in parallel. Each area’s Fast suite must finish in under 30 s wall time, measured after compilation on an admitted dev-host slot; shared-fixture startup and disposal remain inside the measurement; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. The 2026-11-04 user orders are: "let's make the test run under 30" and "update all instructions and rules for that".

The 2026-11-04 user order narrows both suites to the happy path and critical paths: "I just want to test the happy path and critical paths. Everything else needs to be deleted." See `.cos/verify-20261103/test-policy.md` in the workspace. Delete at least 80% of tests against that policy’s baseline.

Fast retains short integration paths for hosted passcode sign-in, the OIDC code flow, access-token contracts, scopes and revoked keys, tenant refusal, backend/workload grants, erasure, email delivery and branding, health and OpenAPI/Scalar.

Heavy critical journeys run in Slow with their complete assertions: MFA enrollment/sign-in/recovery, global sign-out, email-change revocation, customer-project sign-in/refresh, SSO, device approval/replay, invitation acceptance/revocation/audit, organization role and ownership safeguards, broad key/token exchange permissions, password signup/reset, attempt-budget enumeration refusal and cross-replica code/refresh races, key rotation, project-closure lost-answer replay and the complete tenant user/session permission matrix. The admitted 49-case candidate passed every assertion but measured 27.299 s overall and 14–18 s for these broad journeys. Keeping them unchanged in Slow preserves the Keep-list coverage while reducing concurrent fixture work in Fast.

Slow also retains TerraBase shutdown/crash recovery, regional storage outage, history erasure, deletion cleanup across restarts, credential survival across auth restart, three-replica token validation, cross-replica sign-in revocation, the complete social identity lifecycle and the NuGet vulnerability gate. New features add at most one happy-path test and one critical-failure test. Delete noncritical edge permutations and copy assertions rather than moving them to Slow.

The source-linked harness shares one real TerraBase process per test assembly. Ordinary management and token journeys borrow from four assembly-owned Auth hosts, each with its own database. This avoids serializing the parallel journeys on one database writer; each journey also owns unique synthetic users and projects. Custom clocks, key mutations, restarts and host-wide observations retain owned hosts and databases. OIDC hosts wait for real startup provisioning once. Successful fresh key reads clear an earlier failed-read backoff, including readiness reads made before startup created the collections; bootstrap needs no wall-clock delay. Fast uses TUnit’s default full parallelism, without the old one-test-per-CPU override. Shared host initialization belongs to the assembly fixture and remains included in the suite wall time.

Measure with:

```sh
ts-heavy-any make auth-test-fast TEST_ARGS="--report-trx --report-trx-filename auth-fast.trx" --repo terrascale --branch main
```

Keep per-test durations and comparable results under [`benchmark/bench/auth-fast-20261029`](../../benchmark/bench/auth-fast-20261029/).
