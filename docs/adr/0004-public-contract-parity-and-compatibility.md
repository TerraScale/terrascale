# ADR 0004: terrabase-published public contract parity

Status: Accepted boundary; the hand-copied route binding was deleted; the pinned contract (product item P-2b) is the mechanism.

Date: 2026-08-29

## Context

`terrabase` is the Rust engine and the owner and producer of TerraScale's
public database and reactive API. Its versioned REST and reactive contracts
are the only server contracts that the customer SDKs consume. TBEP is the
private protobuf/gRPC control protocol for the orchestrator and CLI; it is not
on the customer request path and is not a .NET customer SDK surface.

This repository owns only the customer-facing .NET SDK. It does not own a
server, gateway, adapter, or private control-protocol client.

## Decision

The .NET SDK implements only named customer operations published by
terrabase. Each operation binds method, versioned path or frame, operation
identity, parameters, media types, typed models, nullability, security scope,
limits, errors, partial outcomes, idempotency, retry, pagination, checkpoints,
and cancellation behavior.

terrabase publishes `openapi.json`, the MessagePack manifest, and TBEP protos
as a versioned contract package (D-33). The SDK pins that package and
generates its public bindings from it; CI fails on drift. A missing or changed
public operation blocks the affected SDK member or requires an explicit,
versioned compatibility decision. No generic request, arbitrary path, custom
header, private route, direct engine path, or TBEP escape hatch may hide a
contract gap.

## Exit evidence

Every published member needs a parity record and success plus expected-failure
tests against the same terrabase schema revision. Breaking behavior requires
an explicit version and migration record. The SDK must not retain clients,
models, routes, or tests for operations that terrabase no longer publishes.
