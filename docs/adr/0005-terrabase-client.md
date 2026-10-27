# ADR 0005: TerraBase client replaces Native

Status: Accepted boundary.

Date: 2026-10-04

## Context

The C# TerraBase server publishes the Documents API v1 and the Management API
over its HTTPS client listener. The `Native` slice targets the retired Rust
UUID/shard API. ts-auth needs a TerraBase database client for its
store.

## Decision

`TerraScale.DotNet.TerraBase` is the SDK's database client. Its Documents and
Management clients follow TerraBase's published contract at the pinned server
revision `ff256067015433c8c40b59879f1282d2a40480ce`; they add no route, field
or header the server does not document. TerraBase rejects `Idempotency-Key`, so
requests are sent once, except the 503 refusals TerraBase documents as answered
before execution with `Retry-After` (`remote_region_unavailable`,
`confirmation_busy`, `catalog_stale`) and, for reads, `429 tenant_busy`, the transient
503s `auth_unavailable`, `storage_busy` and `capacity`, gateway 502/504 and lost connections.
Optimistic concurrency uses document versions through `If-Match` and
`If-None-Match: *`.

`Native` is legacy. It stays so existing consumers compile and receives no new
features. Shapes and the bulk data endpoints are out of scope until TerraBase
publishes them.

Tests never use a fake TerraBase (human decision, 2026-10-04): every TerraScale
service persists in the same production TerraBase release end users use.
Superseded 2026-10-11 (user rule: no repository other than the monorepo depends on
TerraBase): this repository tests the clients over HTTP fakes against the route
manifest `contracts/terrabase/routes.json`; the monorepo runs ts-auth's suites
against the TerraBase built from the same commit and checks it serves every route
in the manifest. `TerraScale.DotNet.Testing` and the pinned release are deleted.

## Exit evidence

The monorepo's ts-auth Fast suite (TerraBaseSdkContractTests and the store tests)
runs against the TerraBase built from its own commit at the pinned ts-dotnet revision.

## Update (2026-10-12)

`Native` and its Atlas admission/discovery are deleted: TerraBase serves none of the native routes
(`/v1/tenants/{t}/databases/{d}/shards/{n}`) and Atlas is being removed. The auth client's Atlas discovery and
`UseReverseProxy` switch are deleted too; it calls its `BaseAddress` directly. Greenfield: no shim.
