# Reactive client transition

Status: Proposed for the replacement public transport. The current client
behavior is implemented legacy evidence, not the final production contract.

Date: 2026-08-29

## Context

The workspace goal requires an authorized result-view protocol over a bounded
public WebSocket. The current repository still has a legacy v1 client and a
v2 HTTP/JSON plus SSE session carrying opaque native payload bytes. The
generated MessagePack module is not yet connected to that live session.

## Proposed decision

Adopt the owner-published `terrascale.sync.v2.msgpack` contract generated from
the shared manifest. The production client will subscribe to bounded typed TQL
views, apply authorized snapshot and result operations, persist opaque view
checkpoints, recover through snapshot/reset when history expires, and send
durable mutations only while connected. Optimistic presentation remains
reversible and tied to one idempotency identity. The client will handle auth
expiry, permission changes, Region movement, slow consumers, backpressure,
reconnect, cancellation, and typed errors.

`TerraScale.Database`, whose permanent source home is `TerraScale.Internals`
while the service remains independently deployable, must publish the WebSocket
schema, route, and cutover/deprecation record. TerraBase remains the reference for shared
semantics and corpus vectors. TerraScale.TypeScript and TerraScale.DotNet own
their bindings. Do not switch the live client until those bindings agree, the
Rust/TypeScript/.NET corpus is bidirectionally green, and live recovery
evidence exists. Do not preserve old JSON, SSE, Loro, or private engine payload
semantics as a second production protocol.

## Consequences

This proposal requires coordinated SDK, Database API, TerraBase, and .NET
work. It changes observable wire and reconnect behavior, so the cutover,
deprecation window, and compatibility promise require owner ratification.
Until then, documentation must describe `src/sync/v2/session.ts` as its
current HTTP/SSE implementation and must not call it the final MessagePack
transport.
