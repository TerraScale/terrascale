# Auth Fast coverage and runtime

Auth Fast runs only the happy path and critical paths in parallel. Each area’s Fast suite must finish in under 30 s wall time, including `make auth-test-fast` on a dev host; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. The 2026-11-04 user orders are: "let's make the test run under 30" and "update all instructions and rules for that".

The 2026-11-04 user order narrows both suites to the happy path and critical paths: "I just want to test the happy path and critical paths. Everything else needs to be deleted." See `.cos/verify-20261103/test-policy.md` in the workspace. Delete at least 80% of tests against that policy’s baseline.

Fast has one happy-path test per public feature: sign-up, hosted sign-in, the OIDC code flow and tokens, social and SSO sign-in, MFA, organizations, projects, invitations, API keys, CLI device login, email delivery and branding, account and project deletion, the workload grant, health and OpenAPI/Scalar. It also covers the critical paths: token and API-key validation, revoked keys and sign-ins refused (also across replicas), refresh and code replay, cross-tenant isolation, and storage unavailable failing readiness.

Slow keeps only heavy critical tests: TerraBase shutdown and crash recovery, regional storage outage failing closed, history erasure, deletion cleanup across restarts, the shared code-attempt budget, credential survival across an auth restart, three-replica token validation, cross-replica sign-in revocation, the complete social identity lifecycle, and the NuGet vulnerability gate. New features add at most one happy-path test and one critical-failure test. Delete edge permutations, validation matrices and copy assertions rather than moving them to Slow.

The source-linked harness shares one real TerraBase process per test assembly. Ordinary management and token journeys share an assembly-owned Auth host and database; each owns unique synthetic users and projects. Custom clocks, key mutations, restarts and host-wide observations retain owned hosts and databases. OIDC hosts wait for real startup provisioning once. Successful fresh key reads clear an earlier failed-read backoff, including readiness reads made before startup created the collections; bootstrap needs no wall-clock delay. Fast uses TUnit’s default full parallelism, without the old one-test-per-CPU override. Shared host initialization belongs to the assembly fixture and remains included in the suite wall time.

Measure with:

```sh
ts-heavy-any make auth-test-fast TEST_ARGS="--report-trx --report-trx-filename auth-fast.trx" --repo terrascale --branch main
```

Keep per-test durations and comparable results under [`benchmark/bench/auth-fast-20261029`](../../benchmark/bench/auth-fast-20261029/).
