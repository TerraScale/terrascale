# Public protocol bounds

Status: Accepted from the workspace goal; service-specific schemas still need
to publish matching limits.

Date: 2026-08-29

## Decision

The SDK targets the workspace public ceilings: 8 MiB documents, 16 MiB HTTP
requests or batches, 1,000 mutations per batch, nesting depth 64, 8 MiB per
MessagePack message, 4 MiB chunks, query pages of 100 by default and 1,000
maximum, query time of 5 seconds by default and 30 seconds maximum,
relationship traversal depth 5, 16 KiB tokens and checkpoints, 128 live
subscriptions per connection, and 32 in-flight operations per connection.

The owner-published REST and reactive schemas are authoritative for operation
specific limits. Compression does not raise a limit. A client rejects hostile
input before constructing an unbounded public value and does not silently
retry deterministic validation, authentication, authorization, or conflict
failures.

## Current evidence and gap

The checked-in MessagePack manifest and decoder enforce the 8 MiB message,
4 MiB binary, depth 64, 16 KiB string, and 1,024-item structural scan bounds,
with stricter field limits where declared. The current HTTP/SSE session and
REST clients still require contract-by-contract comparison with the Database
API schemas. A bound is not production-qualified until the service, both SDKs,
and live tests agree.
