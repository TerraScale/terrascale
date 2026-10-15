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

## Sizing the OIDC project (operator)

Enrollment (`ResolveProjectAsync(create: true)`) writes a storage organization with a random id, the workload as
`CreatedBy` and no members, so `PUT /v1/management/organizations/{organization}/plan` (operator acting as an owner) can
never reach it, and the hot-database burst (3000 writes, 32 in parallel) cannot finish within the retry deadline on hobby.
`PUT /admin/projects/{project}/plan` sets that organization's plan. Like `POST /admin/api-keys` it is served only on the
internal listener on the machine's private 6PN address, like `POST /admin/api-keys`, so it grants nothing to the workload: the token's scopes, project
isolation and owner-less organization are unchanged. It answers 404 for an unknown project and for a project bound to an
owned organization (a legacy binding names its authority organization), whose owners keep the plan. It records
`settings.plan_updated` by `operator` in the organization's audit. Catalog writes run on the primary (gru); the plan
reaches sjc through catalog replication. `ProjectTokenTests` covers it: hobby refuses at its concurrency, enterprise
admits the same request, and the client listener, an owned project and an unknown project are refused.

The e2e project is set to enterprise, as the API-key e2e organization and the local cluster are. With the
`TERRABASE_PEER_SECRET` fly secret in the environment (never printed) and the project id from
`fly ssh console -a ts-auth -r gru -C "/app/ts-auth bind-github-actions-e2e"` (`already bound`, ids only):

```bash
umask 077; h=$(mktemp); printf 'X-TerraBase-Peer-Credential: %s\n' "$TERRABASE_PEER_SECRET" > "$h"
flyctl proxy 18082:8081 gru.ts-terrabase.internal --app ts-terrabase > /dev/null 2>&1 & p=$!; sleep 3
curl -s --http2-prior-knowledge --header "@$h" http://127.0.0.1:18082/healthz | jq -r .region   # gru
curl -sS --fail --http2-prior-knowledge --header "@$h" -H 'content-type: application/json' -X PUT \
  -d '{"plan":"enterprise"}' "http://127.0.0.1:18082/admin/projects/$PROJECT/plan" | jq -r '.id + " " + (.plan|tostring)'
kill $p; rm -f "$h"
```
