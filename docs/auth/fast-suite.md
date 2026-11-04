# Auth Fast coverage and runtime

Auth Fast runs the main feature flows in parallel and must finish within five minutes, including `make auth-test-fast` on a dev host and the Auth CI job. The 2026-10-29 user order is: "fast tests needs to be parallelized and run in under 5 minutes and tests all the main features only".

The 2026-11-04 user order narrows both suites to the happy path and critical paths: "I just want to test the happy path and critical paths. Everything else needs to be deleted." See `.cos/verify-20261103/test-policy.md` in the workspace.

Fast has one happy-path test per public feature: sign-up, hosted sign-in, the OIDC code flow and tokens, social and SSO sign-in, MFA, organizations, projects, invitations, API keys, CLI device login, email delivery and branding, account and project deletion, the workload grant, health and OpenAPI/Scalar. It also covers the critical paths: token and API-key validation, revoked keys and sign-ins refused (also across replicas), refresh and code replay, cross-tenant isolation, storage unavailable failing readiness, and a restart keeping credentials valid.

Slow keeps only heavy critical tests: TerraBase shutdown and crash recovery, regional storage outage failing closed, history erasure, deletion cleanup across restarts, the shared code-attempt budget, and the NuGet vulnerability gate. New features add at most one happy-path test and one critical-failure test. Delete edge permutations, validation matrices and copy assertions rather than moving them to Slow.

The source-linked harness shares one real TerraBase process per test assembly. Each test retains its own database and synthetic data. Input vectors that use one configuration share their test-owned Auth host. OIDC hosts wait for real startup provisioning once. Successful fresh key reads clear an earlier failed-read backoff, including readiness reads made before startup created the collections; bootstrap needs no wall-clock delay. Default Fast concurrency follows `nproc`.

Measure with:

```sh
make auth-test-fast TEST_ARGS="--report-trx --report-trx-filename auth-fast.trx"
```

Keep per-test durations and comparable results under [`benchmark/bench/auth-fast-20261029`](../../benchmark/bench/auth-fast-20261029/).
