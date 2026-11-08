# ts-auth

ts-auth owns identity and access: users, sessions, organizations, projects, memberships, invitations and customer API credentials. TerraBase validates its signed project tokens offline. Dashboard calls ts-auth's APIs rather than reading its storage.

The service is C# on the exact .NET SDK in `global.json`. Its state is SQL tables in its own TerraBase database, reached through the .NET SDK. Startup provisions the required tables; readiness verifies schema and a synthetic conditional write/read/delete without provisioning. See [SQL storage](pivot/p1-sql-storage.md) and [platform bootstrap](platform-bootstrap.md).

## Contracts

The served `/openapi/v1.json` and `/scalar` describe current HTTP operations. Public APIs use `/v1` and generated IDs are ULIDs. Features are always on; no test provider or authentication bypass belongs in a production binary.

- [Platform ownership and permissions](platform-contract.md).
- [Consumer integration](consumer-integration.md).
- [Project tenant management](project-tenant-management.md).
- [Scoped end-user tokens](scoped-end-user-tokens.md) for authorized SQL shapes.
- [Project social sign-in](project-social-sign-in.md).
- [API-key lifecycle](regional/api-key-lifecycle.md).
- [Signing keys](signing-keys.md), [MFA](mfa.md) and [MFA sign-in](mfa-sign-in.md).
- [Step-up proof](step-up-proof.md) and [email change](email-change.md).

## Storage and security

Each entity type has a SQL table. Conditional mutations guard the row's opaque version and run atomically with uniqueness, credential and closure assertions. A storage or authority failure fails closed; a process cache never grants authority. Runtime secrets come from OCI Vault through instance principal; CI secrets belong in GitHub Actions.

Signing keys are encrypted under the master key, published through JWKS and rotated by the service. Password hashes, API-key digests, challenge claims and deletion fences retain their security boundaries. Short-lived tokens do not make credential revocation instantaneous; callers must follow each token kind's lifetime and revocation contract.

Account deletion spans independent products. Auth records only its verified result, and dashboard acknowledges cleanup through the private deletion handoff. Logical deletion, retained backups, key destruction and physical erasure are separate guarantees.

- [Account-deletion receipts](account-deletion-receipts.md).
- [Dashboard deletion handoff](dashboard-deletion-handoff.md).
- [Restart survival](operations/restart-survival.md).
- [Private response cache policy](../security/private-response-cache-policy.md).

## Development

Use `make auth-build`, `make auth-test-fast`, `make auth-test-slow` and `make auth-check` from the monorepo root. CI runs the Fast suite on every push to main. Fast uses the default full parallelism and isolated synthetic data; see [Fast suite](fast-suite.md).

Deploy only green main. SDK publishing, legal terms and secret rotation for launch remain [deferred](../decisions/go-live.md).
