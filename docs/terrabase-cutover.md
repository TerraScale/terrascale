# TerraBase state cutover

COS-run checklist for moving dashboard state from the in-memory demo to
TerraBase (TS-472). At runtime the dashboard uses only the ts-go Documents v1
client against one dedicated database. It never calls the TerraBase
Management API; only the operator `import-org` command below reads it.

## 1. Inputs from TS-529

`ts-orch ouroboros bootstrap` (TS-529) provisions, on production TerraBase:

- organization `terrascale`, project `ts-dashboard`, database `dashboard`;
- a `tb_` key with `documents:read` + `documents:write`, narrowed to that
  project and database, stored in Vault.

Record the project ID and the key. Never put the key in git, logs or a
dashboard document.

## 2. Set the Fly secrets

All four TerraBase variables must be set together; a partial set fails startup.
With none set, the dashboard keeps the in-memory demo store, so merging this
change does not alter a deployment without them.

```sh
fly secrets set --stage \
  TERRABASE_URL='https://<terrabase-origin>' \
  TERRABASE_API_KEY='tb_<key-from-vault>' \
  TERRABASE_PROJECT_ID='<ts-dashboard-project-id>' \
  TERRABASE_DATABASE='dashboard' \
  TS_DASHBOARD_CSRF_SECRET='<stable secret, at least 32 bytes>'
```

`ts-orch ouroboros fly-secrets` stages the `TERRABASE_*` values the same way.
`TS_DASHBOARD_PRODUCTION_DEMO=1` in `fly.toml` may stay: the TerraBase
variables take precedence over it. Billing stays disabled until the Stripe
variables are set; Stripe bindings live in the same `dashboard` database.

## 3. Import the organization

From an operator host that can reach the TerraBase Management listener (the
loopback admin listener while TS-519 applies), with the four variables above
exported:

```sh
export TERRABASE_MANAGEMENT_URL='http://127.0.0.1:<admin-port>'   # default: TERRABASE_URL
export TERRABASE_MANAGEMENT_CREDENTIAL='<dashboard service credential>'
export TERRABASE_MANAGEMENT_USER_ID='<member of the org, e.g. the ts-orchestrator identity>'
nice -n 19 go run ./cmd/dashboard import-org --org terrascale --owner-email mariogk@terrascale.tech
```

The command is idempotent:

1. creates the state collections and seeds the plan catalog when empty;
2. finds the owner account by email, creating it without a password when it
   does not exist (the first OIDC sign-in links it, TS-492);
3. mirrors the organization, its projects and databases under their
   TerraBase IDs. Reruns rename and add; they never duplicate or delete.

## 4. Deploy and verify

```sh
fly deploy
curl -s https://<dashboard>/health/ready
```

Readiness must report `"backend":"terrabase"`; it reads one `users` document
and returns 503 when TerraBase is unreachable. Sign in as the owner and check
the `terrascale` organization lists `ts-auth` and `ts-dashboard`.

## 5. Rollback

Unset the secrets, or redeploy the previous image; both return to the memory
store:

```sh
fly secrets unset TERRABASE_URL TERRABASE_API_KEY TERRABASE_PROJECT_ID TERRABASE_DATABASE
# or
fly deploy --image <previous-image-ref>
```

Keep the `dashboard` database and its key: dropping collections or rotating
the key strands the migrated state. Rerun the readiness check afterwards.
