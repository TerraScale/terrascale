# SQLite is local development only

Status: Accepted.

Date: 2026-08-29

## Context

The SDK needs a fast local runtime for Dashboard and customer contract
development while TerraBaseV2 and the Database API are being qualified. SQLite
cannot represent the production durability, multi-Region, authorization,
reactive, usage, backup, provider, or performance contract.

## Decision

Keep SQLite behind the explicit `@terrascale/sdk/local` entrypoint and require
`sqlite: { enabled: true }`. Root, `admin`, and `better-auth` clients reject
SQLite configuration. The local runtime may run an explicitly requested
loopback server and semantic tests. It is never a production, CI
qualification, staging, or deployed database mode.

## Consequences

Local semantic tests are useful for route and lifecycle behavior, but they do
not prove TerraBaseV2 replication, RF=2 durability, failover, authorization,
usage, billing, external providers, reactive cross-language parity, or
performance. Release records must label local evidence separately and cannot
substitute it for live service qualification.
