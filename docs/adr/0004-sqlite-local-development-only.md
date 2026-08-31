# SQLite is local development only

Status: Accepted.

Date: 2026-08-29

## Context

The SDK needs a fast local runtime for Dashboard and customer contract
development while TerraBase and the Database API are being qualified. SQLite
cannot represent the production durability, multi-Region, authorization,
reactive, usage, backup, provider, or performance contract.

## Decision

Keep SQLite behind the explicit `@terrascale/sdk/local` entrypoint and require
`sqlite: { enabled: true, databasePath: "/absolute/path/local.sqlite" }`. The runtime rejects
relative paths, in-memory paths, and production, CI, qualification, staging,
or deployment environment indicators before it opens SQLite. Root, `admin`,
and `better-auth` clients reject SQLite configuration.

The HTTP server binds to loopback by default. A non-loopback bind requires
`allowRemote: true`, `authentication: "required"`, and an explicit bearer token
of at least 32 characters. The CLI reads that token only from
`TERRASCALE_LOCAL_BEARER_TOKEN`; it does not print it. Protected routes compare
the supplied Bearer credential with the configured token and do not accept an
arbitrary cookie or nonempty authorization header.

## Consequences

Local semantic tests are useful for route and lifecycle behavior, but they do
not prove TerraBase replication, RF=2 durability, failover, authorization,
usage, billing, external providers, reactive cross-language parity, or
performance. They cannot substitute for live service qualification.
