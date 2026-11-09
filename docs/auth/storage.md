# ts-auth storage

ts-auth keeps all of its state in relational tables of its TerraBase database (`ts-auth` in the platform project
`00000000000000000000000002`). It reaches them through the TerraBase Entity Framework Core provider
([`TerraScale.EntityFrameworkCore`](../../sdk/dotnet/docs/entity-framework-core.md)) over TerraBase's internal
listener, with the peer credential that `TerraBaseTransport` adds to every request on the injected `HttpClient`.

## Model

- One `DbContext`, `AuthDbContext` (`src/TerraScale.Authentication/Storage`), registered with
  `AddPooledDbContextFactory`: the services that use it are singletons, so they create a pooled context per operation.
- One table per entity type, with typed columns in snake case. Keys are ULIDs or the entity's natural key (a token
  digest, a composite of ULIDs); there are no integer keys.
- Every row that is updated concurrently carries a `version` column: a ULID concurrency token rewritten by every
  update. `users.version` is also exposed as `UserRecord.Version`, so a caller that read a user can assert it.
- JSON columns hold only nested values that are read and written whole and never queried: credential lineage
  (`bindings`), scope and redirect lists, OpenIddict properties, email branding and template overrides, and the
  project's hosted-page settings.
- `DateTimeOffset` values are stored as UTC `DateTime` text, so expiry comparisons and ordering run in SQL.

## Constraints

The database enforces the invariants the code relies on:

| Invariant | Constraint |
|---|---|
| One account per email in a tenant | `users` unique `(instance_id, email)`; `email` is lower case |
| One account per external identity in a tenant | `provider_links` primary key `(instance_id, provider, issuer, client_id, subject)` |
| Project names unique in an organization, ignoring case | `projects` unique `(organization_id, name_key)` (`name_key` is the invariant upper case of the name) |
| One pending invitation per address and target | `invitations` unique `(organization_id, target, email) WHERE status = 'pending'`; token digest unique |
| One-use credentials (passcodes, sessions, challenges, refresh tokens, GitHub Actions `jti`) win once | `credential_claims` primary key `(instance_id, kind, key)` |
| Passkey credential ids unique in a tenant | `passkeys` primary key `(instance_id, credential_id)` |
| OIDC reference tokens unique | `oidc_tokens` unique `(instance_id, reference_id)` |
| One SSO connection per organization and domain | `sso_connections` primary key `domain`, unique `organization_id` |
| Rows of a deleted user, organization or project go with it | foreign keys with `ON DELETE CASCADE`: a user's sessions, MFA and passkey credentials, step-up proofs, email changes and provider links; an organization's memberships, grants, API keys, invitations, audit events and SSO connection; a project's grants, API keys, invitations, settings, webhook endpoints and webhook outbox |
| An organization with projects, or a user who is still a member, cannot be deleted | `projects.organization_id`, `memberships.user_id` and `project_members.user_id` are `RESTRICT` |
| Roles, statuses and counters stay in range | check constraints (`ck_*`) |

A unique, primary-key or foreign-key violation and a stale `version` are conflicts: `AuthData.SaveAsync` answers
`false`, and the caller re-reads, retries or answers the API's documented error (`email_taken`, `name_taken`,
`invitation_pending`, `409 conflict`). Any other storage failure fails closed with `503 temporarily_unavailable`.

## Writes

A write is a list of `Change`s (`Insert`, `Update`, `Delete`, `Assert`) saved by one `SaveChangesAsync`, which the
provider sends as one `/sql/execute` request that TerraBase runs as one transaction. `Update` and `Delete` name the
version they read; `Assert` rewrites a row's version to itself, so the save fails if anyone changed that row since it
was read. Sign-in, session issue and touch, token issue and refresh are single requests; no hot path opens an explicit
transaction.

- **Authority.** An organization or project change asserts the caller's membership or grant at the version its role
  check read, so a demotion in between fails the write.
- **Credential lineage.** A credential's bindings are checked by reading the bound user (`auth_stamp`), authorization
  (`status`), provider link or claim, and the write asserts each of those rows.
- **Closures.** Creating an account, organization or project closure intent rewrites the user's, organization's or
  project's version. Every write that adds access to that scope first checks that no closure intent exists and asserts
  the scope row, so it either commits before the intent or fails.

## Migrations and startup

- Migrations live in `src/TerraScale.Authentication/Storage/Migrations` and are committed. Generate one from the
  repository root:

  ```sh
  dotnet tool restore
  dotnet ef migrations add <Name> --project src/TerraScale.Authentication \
      --startup-project src/TerraScale.Authentication.EntityFrameworkDesign --output-dir Storage/Migrations
  dotnet format whitespace src/TerraScale.Authentication/TerraScale.Authentication.csproj
  ```

  `src/TerraScale.Authentication.EntityFrameworkDesign` is the design host; it is never packed, published or referenced.
- Every replica runs `Database.MigrateAsync` at startup (`SigningKeyRotation`), then seeds the platform owner, the
  `TerraScale` organization, its `ts-auth` and `ts-dashboard` projects and the owner's grants
  ([platform bootstrap](platform-bootstrap.md)), registers the configured OIDC clients and provisions the first
  signing key. Concurrent replicas are safe: the provider's migrations lock applies each migration once, and the seed
  is create-only, so the losers read the winner's rows. A fresh, empty database comes up fully working.
- Each migration runs in one TerraBase transaction (at most 5 seconds); keep data moves in their own migrations.
- Readiness requires no pending migration and a synthetic insert, read and delete of a `storage_probes` row.

## Reads and replicas

Reads outside a transaction may run on the region's replica; writes run at the database's home. Every write that
grants, consumes or changes a credential or a role is conditional on the versions of the rows it read, so a decision
taken on a stale read cannot commit.

## Operations

An operator grants the platform admin role through TerraBase's internal listener:

```sql
UPDATE users SET platform_role = 'admin' WHERE id = '<user ULID>' AND instance_id = 'terrascale';
```

Background workers prune expired sessions, passcodes, step-up proofs, email changes, challenges, OIDC tokens and
authorizations with set-based deletes, and drive deletion and closure jobs from `deletion_jobs` and `closure_jobs`.
EF Core's `Microsoft.EntityFrameworkCore` meter is exported with ts-auth's metrics.
