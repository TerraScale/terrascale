# TerraBase state cutover

The dashboard stores persistent state through the ts-go Documents v1 client
against one dedicated database (TS-472). Users' databases live in
TerraBase projects the dashboard makes as the signed-in user, through
Management with their ts-auth access token
([store-contract.md](store-contract.md#user-databases-ts-671)), with
a trusted ts-auth issuer binding delivered automatically.

Platform admin sign-in also reads
system database inventory through ts-go Management, and system console pages
read those databases with the signed-in user's ts-auth access token.

ts-auth owns users, organizations, projects, memberships, invitations and API
keys. The dashboard owns platform preferences, plans, subscriptions, Stripe
billing and spending caps. The existing dashboard identity records and admin
inventory mirror are compatibility state pending the
[TS-599 platform cutover](platform-cutover.md).

## Required runtime contract

Persistent state, real ts-auth authorization and billing are always available.
Endpoints and database scope are discovered automatically; OCI Vault must provision
and deliver database, client and stable CSRF secrets. There is no production
demo mode, backend-selection step or Stripe activation variable.

The current `TERRABASE_*`, `TS_AUTH_*` and demo paths remain implementation gaps.
This docs change implements neither their removal nor automatic provisioning.
Missing storage/credentials must fail closed, with no memory-store fallback.

## Provisioning boundary

The ts-orchestrator bootstrap pipeline (TS-529) provisions the service's
TerraBase project, database and access credential. State collections and the
plan catalog must already exist before the dashboard serves traffic.
`EnsureCollections` and `SeedPlans` remain provisioning/test-fixture helpers;
neither application startup nor readiness creates collections or seeds plans.
The dashboard has no provisioning or identity import CLI.

## System console access

An account must first exist through normal verified sign-in. Until ts-auth
serves `platform_admin`, the local grant remains necessary to open `/admin`.
On a dashboard machine configured with the database variables above:

```sh
fly ssh console -C "/dashboard grant-platform-admin --email mariogk@terrascale.tech"
```

`--revoke` reverses the grant. The command is idempotent and records changes
in the admin action log; it does not create accounts, collections or plans.

At the platform admin's next ts-auth sign-in, `MirrorOrganizations` reads every
TerraBase organization they belong to, including its projects and databases,
into their existing dashboard account under TerraBase IDs. A missing account
returns `core.ErrNotFound` without creating an identity. Repeated mirrors add
or rename inventory without duplicating or deleting it. A mirror failure is
logged without blocking sign-in. System databases remain read-only, with
secrets masked in the console.

## Verification and remaining cutover

`GET /health/ready` must report `"backend":"terrabase"` for persistent
deployments. It reads one `users` document and returns 503 when TerraBase is
unreachable. After granting access, sign in again and check that the
`terrascale` organization lists the service databases visible to that admin.

Removing the identity import CLI does not complete TS-599. The full cutover
still requires ts-auth billing/usage scopes, `platform_admin`, project
suspension and MCP OAuth support, plus TerraBase project JWT authorization.
Keep system admin access and the automatic inventory mirror until those
contracts and the public SDK cross-product flow are verified.
