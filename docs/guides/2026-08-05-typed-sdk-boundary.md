# Typed SDK boundary

`@terrascale/sdk` now exposes TerraScale operations only through typed clients
created with `createTerraScaleClient`. The root package no longer exports
`TerraScaleHttp` or request option types, and `TerraScaleClient.request` has
been removed.

This guide records the boundary decision in [ADR 0002](../adr/0002-typed-public-sdk-boundary.md).
The current route and OpenAPI status, including unresolved cross-repository
drift, is tracked in the repository [GOAL.md](../../GOAL.md).

## Migration

Replace arbitrary `client.request({ method, path, ... })` calls with the
matching typed SDK operation, such as `client.regions.list()` or
`client.databases.create(...)`. If no typed public operation exists, do not
construct a raw request; request that public REST and SDK capability.

Internal SDK modules continue to use the private HTTP transport. Package
consumers cannot import it through the package export map.

## Runtime transport privacy

An independent review found that TypeScript constructor parameter properties
left a discoverable runtime `http` field on every returned subclient, allowing
JavaScript consumers to reach the transport's generic `request` method.
Subclients and `AdminClient` now retain their transport in ECMAScript `#http`
fields, which are not discoverable from package consumers. The built-package
boundary test verifies every public subclient and `AdminClient` has no `http`
field or reachable `request`, while `client.regions.list()` still performs its
typed public request.
