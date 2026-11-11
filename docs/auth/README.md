# ts-auth

ts-auth owns identity and access: users, sessions, organizations, projects, memberships, invitations and customer API credentials. TerraBase validates its signed project tokens offline. Dashboard calls ts-auth's APIs rather than reading its storage.

The service is C# on the exact .NET SDK in `global.json`. Its state is relational tables in its own TerraBase database, reached through the TerraBase EF Core provider. Startup applies the committed migrations and seeds the platform organization; readiness requires no pending migration and a synthetic write/read/delete. See [storage](storage.md) and [platform bootstrap](platform-bootstrap.md).

## Contracts

The served `/openapi/v1.json` and `/scalar` describe current HTTP operations. Public APIs use `/v1` and generated IDs are ULIDs. Features are always on; no test provider or authentication bypass belongs in a production binary.

- [Storage](storage.md): tables, constraints, writes and migrations.
- [Platform ownership and permissions](platform-contract.md).
- [Consumer integration](consumer-integration.md).
- [Project tenant management](project-tenant-management.md).
- [Scoped end-user tokens](scoped-end-user-tokens.md) for authorized SQL shapes.
- [Project social sign-in](project-social-sign-in.md).
- [End-user lifecycle webhooks](webhooks.md).
- [API-key lifecycle](regional/api-key-lifecycle.md).
- [Signing keys](signing-keys.md), [MFA](mfa.md), [MFA sign-in](mfa-sign-in.md) and [Passkeys](passkeys.md).
- [Step-up proof](step-up-proof.md) and [email change](email-change.md).

## Storage and security

Each entity type has a table with typed columns, keys, foreign keys, unique indexes and check constraints. Conditional mutations guard the row's ULID version and commit atomically with the credential and closure assertions they depend on. A storage or authority failure fails closed; a process cache never grants authority. Runtime secrets come from OCI Vault through instance principal; CI secrets belong in GitHub Actions.

Signing keys are encrypted under the master key, published through JWKS and rotated by the service. Password hashes, API-key digests, challenge claims and deletion fences retain their security boundaries. Short-lived tokens do not make credential revocation instantaneous; callers must follow each token kind's lifetime and revocation contract.

Account deletion spans independent products. Auth records only its verified result, and dashboard acknowledges cleanup through the private deletion handoff. Logical deletion, retained backups, key destruction and physical erasure are separate guarantees.

- [Account-deletion receipts](account-deletion-receipts.md).
- [Dashboard deletion handoff](dashboard-deletion-handoff.md).
- [Restart survival](operations/restart-survival.md).
- [Private response cache policy](../security/private-response-cache-policy.md).

## Development

Use `make auth-build` and `make auth-check` from the monorepo root. CI builds and checks ts-auth on every PR that changes it and runs the journey against it ([`e2e/AGENTS.md`](../../e2e/AGENTS.md)).

Deploy only green main. SDK publishing, legal terms and secret rotation for launch remain [deferred](../decisions/go-live.md).
