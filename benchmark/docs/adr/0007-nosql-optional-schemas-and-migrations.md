---
status: accepted
---

# ADR 0007: NoSQL with optional schemas and migrations

Parent record of charter PD-2026-08-26-04 at repository level,
2026-08-26, accepted on user instruction.

Owner: the parent fleet orchestrator, on the workspace owner's product
direction and the accepted W3-F family contract.

## Context

The workspace owner directed that TerraBaseV2 stays NoSQL but supports
schemas and migrations where needed. The accepted family contract
already defines `FamilyKind::SchemaValidation` (PD-2026-08-24-01 D4),
and the Platform surface already carries a typed Schemas concept
upstream of the engine. V1 has no enforced document-schema validation,
so schema enforcement is a V2 capability whose parity rows need explicit
dispositions rather than silent scope changes.

## Decisions

### D-1 — schema-optional NoSQL

The model is unchanged: documents stay schema-free by default, and
nothing makes a schema mandatory for reading or writing a collection.
Adopting a schema never changes a collection's stored model or
V1-parity schema-free behavior. Rejected: mandatory schemas (contradicts
the NoSQL model and V1 parity).

### D-2 — typed, versioned collection schemas on write

A collection may declare a typed, versioned validation contract enforced
at the engine seam on write, building on the accepted
`SchemaValidation` family kind. Validation is bounded, deterministic,
tenant-scoped, and reported through a typed error vocabulary. The wire
format is decided with evidence in the implementation ticket; the
working default is a typed schema language compatible with a documented
subset of JSON Schema for interoperability. Rejected: adopting JSON
Schema verbatim (unbounded constructs, ecosystem drift).

### D-3 — migration contracts distinct from Logical Migration

Schema evolution moves a collection between schema versions under an
explicit engine contract: declarative transforms first, programmatic
escape only through typed operations. Migrations are idempotent,
resumable, and prove manifest, checksum, count, and rollback-limit
evidence under the recovery-lane discipline. This is a different concept
from the manifest-verified V1→V2 Logical Migration, which is unchanged;
the glossary keeps the two terms separate. Rejected: treating schema
migration as a Logical Migration instance (different source, contract,
and evidence).

### D-4 — typed-only exposure

Schemas and migrations surface exclusively through typed operations
(TBEP → C# API host → studio and CLI). No generic schema blob endpoint,
no raw validation surface, no private-route exposure. Parity rows for
schema behavior receive WS-PARITY dispositions with this ADR as their
decision record.
