---
status: accepted
---

# TQL, the TerraBase query language

## Decision

TQL is TerraBase's only native query language. It is document-oriented,
typed, versioned, deterministic, bounded, and independent from RQL or SQL
compatibility. TQL text parses to a canonical typed AST before authorization,
planning, Auto-Index selection, execution, or reactive dependency tracking.

The first grammar is `tql/1.0`. Text, persisted queries, AST frames, TBEP,
public APIs, query plans, and golden vectors name the grammar version. Unknown
major or minor versions and missing versions fail closed. Additive syntax or
semantics increments the minor only after every parser and SDK supports it.
Removal or changed meaning increments the major.

## First-release query scope

TQL supports:

- one collection source;
- equality and inequality filters;
- bounded range and membership predicates;
- boolean composition;
- nested document paths and typed metadata;
- deterministic sorting with document identity as the final tie breaker;
- stable cursor pagination and bounded result windows;
- typed projections;
- bounded count, sum, minimum, maximum, and average aggregates;
- full-text search through a qualified engine-owned index;
- named forward and reverse relationship filtering and expansion;
- bounded multi-hop relationship traversal with explicit depth, result, work,
  and deadline limits; and
- parameters with declared types and byte limits.

The initial product excludes general ad hoc joins, arbitrary graph patterns,
unbounded recursion, spatial search, vector search, custom analyzers, stored
application code, functions, and plugin execution. A future capability enters
through a versioned AST node, planner rule, authorization rule, resource model,
Auto-Index capability, reactive dependency contract, and evidence. It never
appears as an untyped escape hatch.

Collections, not relational tables, are sources. TQL has no SQL wire or SQL
semantic promise. It may use familiar selection, filtering, ordering, and
projection words when their document semantics are explicit.

Typed metadata includes stable document ID, collection, incarnation, and
last-modified HLC. It does not expose a RavenDB change vector, raw CRDT context,
Replica ID, storage key, or authorization data.

## Determinism

The same grammar, canonical AST, arguments, authorization scope, schema
catalog, causal cut, and index generation produce the same ordered result and
result digest.

TQL contains no implicit current time, random source, locale-sensitive
comparison, arrival-order tie, or process-specific value. Time, locale, and
other variable inputs are explicit typed parameters. String comparison,
numeric conversion, null ordering, full-text analyzer identity, and aggregate
overflow behavior are versioned semantics.

Query results name their causal cut. Multi-Partition execution selects a
dependency-closed set of MVCC snapshots. It does not invent a global total
order.

## Parsing and execution safety

The parser and planner bound input bytes, token count, nesting, expression
count, path depth, argument bytes, result rows, projection bytes, relationship
fanout, aggregate groups, estimated index work, memory, and deadline before
execution. Malformed or excessive input returns a typed error without
allocating work proportional to an attacker-controlled declared size.

TQL never executes JavaScript, WASM, shell, a dynamic expression language, or
user-provided bytecode. Its function registry is closed and versioned. Each
function declares accepted types, determinism, index capability, authorization
effects, cost model, and reactive dependency behavior.

Authorization compiles into the same typed plan before index access. Filtering
after an unrestricted query is not acceptable. Projection removes forbidden
fields before result materialization and synchronization.

## Automatic indexes

Ordinary TQL never requires a customer-created index. The optimizer uses the
engine-owned identity path, mandatory relationship indexes, existing explicit
indexes, or query-shaped Auto-Indexes under ADR 0010.

An admitted small query may use a bounded scan while starting an Auto-Index.
A larger unindexed query waits within its deadline or returns
`IndexBuilding`. No syntax requests an unbounded scan, bypasses resource
admission, names an internal index implementation, or supplies a raw full-text
engine query.

Session-consistent index visibility is the default. The chosen index
generation must include the caller's causal dependencies. Eventual index
visibility requires explicit request.

## Reactive queries

Every reactive TQL plan has a `ViewPlanUlid` under ADR 0027 and a canonical
`ViewPlanDigest` under ADR 0024. The planner records collection, index,
key-range, document, relationship, schema, and authorization dependencies. A
query is reactive only when its result and dependency set fit the admitted
bounds.

Simple indexed plans update incrementally. A bounded plan that cannot prove an
incremental patch may rerun and emit `Reset`. Changing execution strategy does
not change query semantics, authorization, ordering, or checkpoint identity
without a view-generation change.

## Public contract

Public APIs and SDKs expose named query and subscription operations with the
grammar version, TQL text or typed builder output, typed parameters,
consistency, deadline, and result bounds. They do not expose a generic TBEP
frame or direct Member endpoint.

SDK builders lower to the same canonical AST and golden vectors as text. A
builder is not a second query language. Rust, the Go gateway, and public SDK
conformance must agree on canonical AST, parameters, errors, ordering, and
result digest.

## Required evidence

Qualification covers parser and planner fuzzing, canonical AST vectors,
cross-language construction, authorization before access, deterministic
ordering, session-consistent index visibility, causal multi-Partition cuts,
bounded scan refusal, aggregate overflow, relationship limits, reactive
dependency correctness, and tenant isolation under hostile query load.

RQL mappings, V1 query behavior, and RavenDB feature lists are not inputs to
qualification.
