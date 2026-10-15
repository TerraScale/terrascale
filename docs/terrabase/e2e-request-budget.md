# E2E request budget on Fly

The post-deploy `e2e` job writes through the GitHub Actions OIDC project
([github-actions-oidc.md](../auth/github-actions-oidc.md)). TerraBase enrolls that project on its first database create,
and nothing publishes a plan for its organization, so tenant admission applies the default plan: hobby
(`QosPolicy`: 50 requests/s, burst 100, 4 concurrent requests per node). This follows from the default path in
`ITenantDirectory`; the live organization's plan has not been read back through the API. The older API-key e2e
organization (`scripts/e2e/install-ci-credential.sh`) and the local cluster (`Cluster.AuthorizeAsync`) are enterprise.

The parallel suite exceeds that budget, so TerraBase answers `429 tenant_busy` with `Retry-After`. Tenant admission
refuses before the request runs (`QosExtensions`, `CommitEndpoint`), so the suite sends exactly that answer again:

- `Docs.AdmittedAsync` is the one retry. It resends only a 429 whose JSON `error.code` is `tenant_busy` and whose
  `Retry-After` is positive and fits in the remaining 60-second convergence deadline. Any other answer is returned after one
  send: `tenant_memory` (it can come after the request ran), a 503 (`write_outcome_unknown` is never replayed), a
  missing or oversized `Retry-After`, or an unreadable body. Its wait ends with the caller's token.
- Setup and cleanup (`Cluster.CreateCollectionAsync`, the backup database create, `DeleteCollectionAsync`,
  `DeleteDatabaseAsync`) go through it, as the document calls already did.
- `Docs.OpsAsync` builds its body once, so a retried send no longer fails with "The node already has a parent".

`tests/TerraBase.E2E/AdmissionRetryTests.cs` covers this against a real node whose hobby concurrency is held:
a refused collection create and a refused ops PATCH each land once after the retry, and the wait ends with its token.
Harness rows check that every other answer comes back after one send. The tests are `local-only`, so
`make terrabase-e2e-local` now expects 37 tests (it was 27 in [replication-e2e.md](replication-e2e.md)); the deploy filter's 138 is
unchanged.

A plan for the OIDC organization is an operator decision: the plan endpoint requires the platform operator acting as an
organization owner, and the binding's organization has no owner user.
