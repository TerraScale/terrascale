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
or header the server does not document. Requests are sent once because
TerraBase documents no retryable failure and rejects `Idempotency-Key`.
Optimistic concurrency uses document versions through `If-Match` and
`If-None-Match: *`.

`Native` is legacy. It stays so existing consumers compile and receives no new
features. Shapes and the bulk data endpoints are out of scope until TerraBase
publishes them.

Tests never use a fake TerraBase (human decision, 2026-10-04): every TerraScale
service persists in the same production TerraBase release end users use.
`TerraScale.DotNet.Testing` starts the real release binary, pinned by tag and
SHA-256 in `TerraBaseRelease.Pinned`, downloads and caches it, and is shared
with other services' tests.

## Exit evidence

Integration tests run against the pinned TerraBase release binary, verified
by SHA-256, in CI. Consumers pin the ts-dotnet revision and the TerraBase
release together.
