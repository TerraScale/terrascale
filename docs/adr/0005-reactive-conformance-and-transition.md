# ADR 0005: qualify the replacement reactive client before transition

Status: Accepted boundary; implementation and qualification pending.

Date: 2026-08-29

## Proposed decision

The replacement client consumes the owner-published bounded result-view
protocol with generated MessagePack bindings, opaque view checkpoints, typed
snapshot/result operations, reversible online optimistic presentation, and
server-authoritative durable mutations. It handles auth expiry, permission
loss, Region movement, history expiry, duplicate/reorder, reconnect,
cancellation, backpressure, and slow consumers.

The legacy v1 and Loro-era v2 implementations remain migration evidence. They
are not extended as a second current protocol and are not removed until the
replacement has a versioned compatibility and rollback plan.

## Exit evidence

Rust, TypeScript, and .NET vectors must agree bidirectionally, and live Database
API tests must prove authorization, tenant isolation, reconnect, reset, and
history expiry before the replacement is promoted.
