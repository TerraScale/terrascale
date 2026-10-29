# ADR 0004: owner-published public contract parity

> Superseded in part (2026-10-12): the native wire contract, `contracts/native` and Atlas discovery are removed; see ADR 0005's update.

Status: Accepted boundary.

Date: 2026-09-30

## Context

TerraBase owns the native v1 database wire contract. Atlas owns authenticated
scoped native discovery and the separate auth discovery surface. ts-auth owns
its instance management and OIDC contracts. This repository owns their public
.NET clients, not server routes or private control protocols.

## Decision

Native bindings consume the exact tracked inputs in `contracts/native`:
producer OpenAPI, atomic vectors, Atlas wire identity and success/forbidden
fixtures, with recorded provenance. `NativeContract` embeds and exposes these
identities. Native methods bind provisioned UUID scope, unsigned shard,
schema identity, typed values, causal state, atomic preconditions, original
transaction identity, effect hashing, errors, pagination and cancellation to
that contract. No generic request or inferred proxy endpoint conceals a gap.

Database traffic uses authenticated Atlas admission and eligible direct
regional origins automatically; every admitted serving region participates
without an activation step. Test API hosts are not production native origins.
Existing direct/loopback paths are restricted to reviewed isolated qualification
or tests and remain implementation inventory, not normal first-party setup.
They require prior caller admission and never infer eligibility. TerraScale-zone
native origins require canonical single-level nonreserved regional HTTPS hosts.
Atlas authority validation, tenant scope and TLS checks remain separate.

The existing ts-auth feature remains isolated in `AuthService`, including its
own fixed safe diagnostic exception, owner-published regional token validation
and DI registration. Unknown error codes, arbitrary producer messages and
response bodies are omitted; options formatting never includes credentials.
It follows the actual management and OAuth contracts, and has no dependency
on retired database models or routing.

Replaced logical-name database clients, generated v1 wrappers, database proxy
routing, reactive clients and the obsolete EF facade are deleted with their
obsolete tests, materialization scripts and documentation. New EF support
would require an actual public native mapping rather than retaining the old
logical Documents adapter. There are no compatibility aliases.

## Exit evidence

Changed source, package layout and package-only consumers are reviewed and
qualified against immutable identities. Contract and local handler tests do
not establish publication or live-service readiness. Releases require
separate approval and exact qualified package bytes.
