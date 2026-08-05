# Typed SDK boundary

`@terrascale/sdk` now exposes TerraScale operations only through typed clients
created with `createTerraScaleClient`. The root package no longer exports
`TerraScaleHttp` or request option types, and `TerraScaleClient.request` has
been removed.

## Migration

Replace arbitrary `client.request({ method, path, ... })` calls with the
matching typed SDK operation, such as `client.regions.list()` or
`client.databases.create(...)`. If no typed public operation exists, do not
construct a raw request; request that public REST and SDK capability.

Internal SDK modules continue to use the private HTTP transport. Package
consumers cannot import it through the package export map.
