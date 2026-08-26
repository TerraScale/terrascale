---
status: accepted
---

# ADR 0009: TQL, the TerraBase query language

Parent record of charter PD-2026-08-26-08, 2026-08-26, proposed by
ticket TQL-01 and accepted by the parent the same day on review of the
companion records.

Owner: the TQL wave (specification `docs/tql/spec.md`, RQL mapping
`docs/tql/rql-mapping.md`, wave
`specs/2026-08-26-terrabase-v2-tql-tickets.md`).

## Context

TerraBaseV2 needs one canonical query language that every user —
human developer, AI agent, Operations Studio, CLI — reaches for. V1's
RQL is the parity baseline: a proven document query language, but one
that embeds arbitrary JavaScript (projections and `declare function`),
couples its surface to index artifacts (`from index`, Lucene
passthrough), and carries no grammar versioning. V2 also has a hard
charter rule set that shapes the answer: automatic indexing
(PD-2026-08-26-09) removes index selection from the user, the
agent-native rule (PD-2026-08-26-07) requires MCP-exposability under
typed operations, and subscriptions (PD-2026-08-26-05, SE wave)
require queries that replay deterministically. The existing W3-2
typed compile seam (`QueryStore::compile`) is the landing point:
whatever the language is, it parses to a typed AST against that seam.

## Decisions

### D-1 — SQL-familiar surface, not SQL semantics

TQL adopts the SQL clause inventory and order — `select … from …
[load] … where … group by … order by … limit … offset … include` —
with SQL operators, `in`, `between`, `as` aliases, and
case-insensitive keywords. `select` is optional (omitted means
`select *`) so RQL's quick `from orders limit 10` form stays valid.
The familiarity target is "an SQL user is immediately productive",
not SQL compatibility: there is no relational catalog, no relational
join (`load` resolves document references by id), no schema
requirement, and no SQL surface compatibility promise of any kind.

### D-2 — document-native data model

The language is tailored to schema-optional documents: dot paths for
nested objects, array navigation (`[i]` positional, `[]` explicit
any-element) with any-match predicate semantics over arrays, a typed
metadata pseudo-field set (`@id`, `@collection`, `@last_modified`,
`@change_vector`, `@metadata.<member>`), and quoted path segments
that address any legal dynamic field name. Collections, not tables,
are the only source; `@all_docs` crosses them. When a collection
adopts a Document Schema (PD-2026-08-26-04, SM wave) the engine may
use it for planning, but the language never requires one.

### D-3 — RQL-informed, with the proven ideas kept and one clause-order change

From RQL, TQL keeps: collection-targeted `from`, dot paths,
`include` and `load`, search/vector/spatial predicates as plain
functions, out-of-band named `$parameters`, `distinct`, `group by`
with aggregates, `between`/`in`/`all in`, `nulls first/last`, and
the comment and identifier conventions. The deliberate divergence is
clause order: TQL is `select`-first (SQL) rather than `from`-first
(RQL). Every RQL capability maps through
`docs/tql/rql-mapping.md` as direct, translated, or
accepted-difference, and TQL-04 proves the mapping with conformance
vectors feeding WS-PARITY.

### D-4 — no arbitrary code execution; typed expressions replace JavaScript

The language contains no JavaScript, no `declare function`, no
script or plugin hook, and no escape hatch. Projections are typed
expressions over a closed, versioned function registry: object and
array constructors, arithmetic, comparisons, `if`, `coalesce`, and
the registered scalar/aggregate functions. This replaces RQL's
`select { … }` JavaScript projections — the flagship accepted
difference — because code execution cannot be made deterministic,
bounded, version-stamped, or safe to expose unchanged to agents under
the MCP invariant. Sandboxed JavaScript and WASM snippets were
considered and rejected: they are still arbitrary code execution,
with the same security and determinism problems plus a runtime to
maintain.

### D-5 — typed, versioned AST with fail-closed versioning

TQL text parses to a typed AST carrying a grammar version
(`tql/MAJOR.MINOR`, currently `tql/1.0`) at every artifact: parse
input, AST root, TBEP frames, persisted queries and subscription
definitions, golden vectors, MCP payloads, and error payloads. The
engine seam requires the version explicitly and never guesses.
Unknown major, unknown minor, or a missing version is a typed error
(`UnsupportedGrammarVersion` / `MissingGrammarVersion`) — never a
best-effort parse. Additive evolution bumps the minor; removal or
semantic change bumps the major. Unparseable queries fail closed with
typed, positioned errors; no class degrades to a warning.

### D-6 — capability-gated predicate functions

Search (`search`, `boost`, `score`), vector (`vector.search`,
`vector.of`), and spatial (`spatial.*`) predicates are part of the
grammar now — so query text, stored queries, tools, and vectors are
stable — but each is gated on the capability the engine actually
ships. Against an engine without the capability the query fails
closed with `CapabilityUnavailable`; it never silently no-ops or
falls back to a different predicate. Vector search is CP-01's
required competitive row; its TQL surface is fixed by this decision.

### D-7 — deterministic definition, reactive and agent-native consumers

A TQL query is a deterministic, replayable definition: same version,
text, parameters, and data state produce the same result stream. That
property is why the language excludes `random()` and `now()`/`today()`
(time arrives as a parameter). It is what lets TQL compose with
subscriptions for live results (TQL-03 with the SE wave) and be
exposed as one typed MCP operation — `(grammar_version, text,
parameters)` in, typed rows or typed errors out — under the
workspace's authorization, tenant-isolation, confirmation, and audit
rules, with no raw query-string surface beside it.

## Rejected alternatives

- **RQL verbatim.** Maximum parity, but it embeds arbitrary
  JavaScript (violates the no-code-execution rule and the MCP
  invariant's typed-operation discipline), couples users to index
  artifacts that automatic indexing removes, has no grammar
  versioning or fail-closed contract, and its `from`-first order and
  dual spellings (`=`/`==`, optional `as`) fight rather than help SQL
  users. The parity obligation is met by the mapping document, not by
  copying the language.
- **SQL verbatim (or a SQL dialect).** Familiar by definition, but
  wrong model: tables, relational joins, and a required schema do not
  fit schema-optional documents; nested paths, array any-match,
  metadata, and dynamic field names have no natural spelling; and it
  would create an implicit compatibility expectation the surface
  compatibility decision (PD-2026-08-26-10) explicitly rules out.
- **JavaScript projections (RQL-style, or sandboxed).** Kept the
  shaping power but gave up: bounded deterministic execution for
  adversarial input, versioned stable semantics across grammar
  versions, safe agent exposure, and the no-code-execution posture.
  Typed constructors plus a small closed registry cover the
  projection patterns RQL users actually wrote; anything beyond them
  is a typed engine operation, not query text.
- **Lucene/Corax passthrough.** Offers power users full engine
  expressiveness and zero translation cost, but leaks engine
  internals into a public language, breaks under automatic index
  selection, and has no typed error or versioning story. Rejected
  with `lucene()` and `from index` in the mapping.

## Consequences

- TQL-02 implements the zero-dependency, deterministic, bounded
  parser and lowers the AST onto the W3-2 compile seam, which gains
  the language's nested paths, arrays, metadata, parameters,
  grouping, loads/includes, and typed projections.
- TQL-03 exposes the typed TQL operation across TBEP, host, CLI,
  Studio, and MCP, delivers live results through subscriptions, and
  records the Platform `tql`-surface compat decision.
- TQL-04 converts `docs/tql/rql-mapping.md` into conformance vectors
  and WS-PARITY disposition proposals; 42 direct (8 gated), 14
  translated, and 29 accepted-difference rows are on file.
- The registry is the extension point: additions are minor-version
  bumps gated on shipped capability evidence; nothing enters the
  registry speculatively.
- This record proposes; the parent accepts. No engine capability,
  Proof ID, or parity claim exists until the wave's tickets deliver
  their evidence.
