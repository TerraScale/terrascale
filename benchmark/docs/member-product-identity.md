# Member product identity admission

The public member router can admit product credentials through a configured
Better Auth identity authority. Registered member data keys retain their existing
scope checks. Other bearer credentials require a fresh authority response for
every request; there is no successful-admission cache or local fallback.

`IdentityConfig` pins an HTTPS endpoint, exact issuer, exact public API audience,
and a private service credential of at least 32 bytes. The native Rust client
verifies the certificate and hostname against Mozilla roots, follows no redirects,
ignores proxy environment variables, bounds concurrent requests to 32, and limits
an attempt to five seconds and the response body to 16 KiB. Failures deny admission.
Credential contents cannot select the endpoint or TLS trust roots.

The producer is the dashboard's `POST /api/internal/identity/introspect` route.
The member sends `x-terrascale-identity-service-key` and JSON containing `token`,
`audience`, `organizationId`, `projectId`, `databaseId`, `action`, and optionally
`collection`. The organization/project come from the member's configured serving
scope; database and collection come from the router's target. The producer must
verify the credential, live session and consent revocation, and current tenant
membership, intersecting explicit credential database scopes with live role
permissions. Dashboard scopes do not imply database scopes.

An admission must return `active: true`, `subject`, `issuer`, `audience`,
`organizationId`, `projectId`, `databaseId`, `action`, `scopes`, and `expiresAt`
(epoch seconds), plus the same `collection` when requested. The member compares
every binding, requires the explicit `database:read`, `database:write`, or
`database:admin` scope for the action, and rejects expired credentials. It creates
a principal restricted to exactly the requested database, collection, and verb.

This wiring currently uses the member's fixed authoritative serving tenant/project.
Delegated credentials cannot use global routes without a database target, including
database creation/listing and reactive view attachment. Multi-tenant deployment
requires admitted ownership routing before this restriction can be lifted. The
identity producer also requires consensus-backed identity storage to preserve
live revocation and permission reductions across partitions; a local or stale
replica does not satisfy that requirement.

The focused `cargo test -p terrabase-server --lib delegated_identity` suite covers
native TLS, untrusted certificates, hostname mismatch, live revocation, permission
reduction, expiry, issuer/audience and tenant/database binding, authority loss,
and admission/revocation through the actual public router.

Configure the existing public API listener with an explicit nested section:

```toml
[public_api.identity]
endpoint = "https://dashboard.example/api/internal/identity/introspect"
issuer = "https://dashboard.example"
audience = "https://api.example"
service_key_file = "/etc/terrabase/identity-service-key"
```

The credential file must be a regular owner-only file. Startup refuses invalid
UTF-8, invalid service-header bytes, short credentials, and invalid HTTPS pins.
The dashboard configures the matching `TERRASCALE_IDENTITY_SERVICE_KEY`; its
Better Auth issuer and public API resource must exactly match the member pins.
