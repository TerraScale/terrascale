# Public protocol bounds

Status: Accepted from the workspace goal; the `terrabase` contract and live
qualification still need to publish matching limits.

## Scope

The Rust `terrabase` member serves the public REST and reactive API directly.
TBEP is a private control protocol for the orchestrator and CLI, not a customer
transport. Public queries are read-only SQL. Reactive subscriptions use
CDN-backed HTTP long-polling by default; WebSocket is an explicit opt-in.

Date: 2026-08-29

## Decision

The SDK targets the workspace public ceilings: 1 MiB documents, 16 MiB HTTP
requests or batches, 1,000 mutations per batch, nesting depth 64, 8 MiB per
MessagePack message, 4 MiB chunks, query pages of 100 by default and 1,000
maximum, query time of 5 seconds by default and 30 seconds maximum,
relationship traversal depth 5, 16 KiB tokens and checkpoints, 128 live
subscriptions per connection, and 32 in-flight operations per connection.

The `terrabase`-published REST and reactive schemas are authoritative for
operation-specific limits. Compression does not raise a limit. A client rejects
hostile input before constructing an unbounded public value and does not
silently retry deterministic validation, authentication, authorization, or
conflict failures.

## Current evidence and gap

The checked-in MessagePack manifest and decoder enforce the 8 MiB message,
1 MiB parameter, 1 MiB projected-value, depth 64, 256 KiB scan-string, and
10,000-item scan-array bounds, with stricter field limits where declared. The
WebSocket and default CDN-backed HTTP transports still need contract-by-contract
comparison with the `terrabase` schemas. A bound is not production-qualified
until `terrabase`, the SDK, and live tests agree.
