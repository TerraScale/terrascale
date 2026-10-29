# Auth Fast coverage and runtime

Auth Fast runs the main feature flows in parallel and must finish within five minutes, including `make auth-test-fast` on a dev host and the Auth CI job. The 2026-10-29 user order is: "fast tests needs to be parallelized and run in under 5 minutes and tests all the main features only".

Fast covers signup and verification, password and passcode login, recovery, sessions and logout, MFA, OAuth/OIDC issuance and refresh, replay and revocation, tenant isolation, organizations and projects, roles and memberships, invitations and ownership, API keys, account deletion, email branding, health, OpenAPI/Scalar and the workload grant lifecycle.

Edge cases, limits, abuse, exhaustive input matrices, transaction races, fault injection, recovery and timing scenarios belong in `tests/TerraScale.Authentication.Slow`. They remain independently executable with `make auth-test-slow`; moving a scenario preserves its assertions. Adding another variant of an existing behavior should extend Slow.

The source-linked harness shares one real TerraBase process per test assembly. Each test retains its own database and synthetic data. Input vectors that use one configuration share their test-owned Auth host. OIDC hosts wait for real startup provisioning once. Successful fresh key reads clear an earlier failed-read backoff, including readiness reads made before startup created the collections; bootstrap needs no wall-clock delay. Default Fast concurrency follows `nproc`.

Measure with:

```sh
make auth-test-fast TEST_ARGS="--report-trx --report-trx-filename auth-fast.trx"
```

Keep per-test durations and comparable results under [`benchmark/bench/auth-fast-20261029`](../../benchmark/bench/auth-fast-20261029/).
