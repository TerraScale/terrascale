# Proposed CONTEXT.md additions for TQL

Status: proposed by ticket TQL-01 under charter PD-2026-08-26-08.
These are proposed glossary entries only — the integrator merges them
into `CONTEXT.md`; this ticket does not edit that file. The entries
follow the existing house format (term, definition, avoided
synonyms). Date: 2026-08-26.

## Proposed entries

**TQL**:
TerraBaseV2's canonical query language: an SQL-familiar,
document-native surface for schema-optional documents, informed by
V1's RQL, that parses to a typed, versioned AST with no arbitrary
code execution in the language.
_Avoid_: query syntax, SQL dialect, RQL (that is V1's language), TerraQuery

**TQL AST**:
The typed, versioned parse artifact every TQL text lowers to against
the query seam: a closed node set carrying source spans and the TQL
Grammar Version, with validated identifiers, resolved aliases, and
parameter binding slots — never unvalidated strings or executable
code.
_Avoid_: parse tree, query object, syntax tree

**TQL Grammar Version**:
The `tql/MAJOR.MINOR` identity carried by every parsed TQL artifact —
parse input, AST, serialized frames, stored queries, vectors, and
error payloads. Unknown or missing versions fail closed; additive
grammar changes bump the minor and breaking changes bump the major.
_Avoid_: language version, dialect tag, schema version (a Document
Schema concept)

## Notes for the integrator

- The three entries slot under a new "Query language" section or the
  existing "Boundaries and clients" section, at the integrator's
  discretion; the cross-reference to Document Schema (Schema Version)
  is intentional so the two version concepts stay distinct.
- The avoided-synonym "RQL" matters: workspace discussion frequently
  says "RQL" when meaning V1's language, and TQL is deliberately not
  RQL — keeping the names distinct keeps the parity mapping
  (`docs/tql/rql-mapping.md`) readable.
