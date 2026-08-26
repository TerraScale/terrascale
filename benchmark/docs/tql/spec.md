# TQL language specification, grammar version tql/1.0

Status: proposed specification authored by ticket TQL-01 (wave
`2026-08-26-terrabase-v2-tql-tickets.md`) under charter PD-2026-08-26-08
and ADR 0009. This document defines the language only. It creates no
engine capability, no Proof ID, and no parity disposition; RQL
capabilities map through `docs/tql/rql-mapping.md` and the parity
matrix. Date: 2026-08-26.

Owner: the TQL wave. TQL-02 implements the parser and the lowering to
the W3-2 typed compile seam (`docs/query/w3-query-index.md`); TQL-03
exposes TQL across TBEP, host, CLI, studio, and MCP; TQL-04 proves the
RQL parity mapping.

## 1. Goals

1. **SQL-familiar surface.** Anyone who knows SQL reads a TQL query
   immediately: `select … from … where … order by … limit …`, SQL
   comparison operators, `and`/`or`/`not`, `in`, `between`, aliases
   with `as`.
2. **Document-native.** TQL is tailor-made for schema-optional
   documents: nested paths, array navigation with any-element
   matching, engine metadata pseudo-fields, and dynamic
   caller-chosen field names. No table, join, or schema is ever
   required.
3. **RQL-informed, not RQL-copied.** V1 RQL's proven ideas carry
   over: collection-targeted `from`, dot paths, `include`/`load`,
   search and vector and spatial predicates as functions, and
   out-of-band parameterization.
4. **Typed and versioned.** TQL text parses to a typed, versioned AST
   against the existing query seam. Every parsed artifact carries a
   grammar version; unknown or missing versions fail closed. All
   errors are typed; none are silent.
5. **Safe by construction.** No arbitrary code execution exists in
   the language. Projections are typed expressions over a closed,
   versioned function registry — RQL's JavaScript projections are
   deliberately replaced (the flagship accepted difference).
6. **Reactive and agent-native.** A TQL query is a deterministic,
   replayable definition: it composes with subscriptions for live
   results (SE wave) and is MCP-exposable as one typed operation
   under PD-2026-08-26-07.

## 2. Non-goals

- **No arbitrary code execution.** No JavaScript, no `declare
  function`, no expression escape hatch, no plugin or script hooks of
  any kind. This is a hard design rule, not a missing feature.
- **No relational model.** No table aliases over rows, no relational
  joins, no mandatory schema, no column catalog. `load` resolves
  document references; it is not a join operator over relations.
- **No raw engine passthrough.** No Lucene/Corax syntax, no index
  hints, no `from index` targeting. Automatic indexing
  (PD-2026-08-26-09) selects indexes; the language never names one.
- **No mutations.** TQL reads only. RQL's `update` clause is not
  carried over; mutations stay typed, idempotent, confirmable
  operations.
- **No wall-clock dependence.** No `now()` or `today()`. A query that
  depended on server time would not replay identically for live
  results or golden vectors; time is passed as a parameter.
- **No unshipped-capability assumptions.** Search, vector, and
  spatial predicates are part of the grammar but gated on the
  capabilities the engine actually ships (Section 8). Until then they
  fail closed with a typed error.
- **No performance claims.** Nothing here is a speed statement;
  PD-2026-08-26-03 governs all of those.

## 3. Surface design

### 3.1 Clause order

TQL uses the SQL clause order, unlike RQL's `from`-first order:

```text
select … from … [load …] [where …] [group by …] [order by …]
[limit …] [offset …] [include …]
```

Every clause except `from` is optional — `from` is the one mandatory
clause, and an omitted `select` means whole-document projection
(`select *`). This keeps RQL's quick form (`from orders limit 10`)
valid while preserving the SQL order for every shaped query. Clause
order is fixed; a misplaced or repeated clause is a parse error (fail
closed).

### 3.2 Keywords and identifiers

- Keywords are case-insensitive (`SELECT`, `select`, `Select` are the
  same token). Identifiers are case-sensitive.
- A **bare identifier** matches `[A-Za-z0-9_][A-Za-z0-9_-]*` — it may
  start with a digit (V1 field names are caller-chosen and may be
  numeric-leading; the W3-2 grammar note deliberately keeps no
  first-character rule) — and must not be a reserved word and must
  not start with `-` (the tokenizer reads that as a negative literal).
- A **quoted identifier** `"…"` names any other field: spaces,
  Unicode, leading digits already covered, embedded dots, or a
  reserved word. `""` inside escapes one `"`. Quoted identifiers are
  how TQL addresses dynamic fields (Section 6.4).
- **Comments** follow V1: `//` to end of line and `/* … */` blocks;
  comments are not part of the parsed artifact.
- **Strings** are single-quoted; `''` inside escapes one `'`.
- **Numbers** are unsigned decimal literals with optional fraction
  and exponent; a leading sign is a unary operator, not part of the
  literal.

### 3.3 Reserved words

```text
select distinct from where group by order limit offset load include as
and or not in all between nulls first last asc desc ascending
descending true false
```

The top-level names `vector` and `spatial` are reserved as function
prefixes; an alias or collection may not take either name.

## 4. Clause inventory

| Clause | Role | Notes |
| --- | --- | --- |
| `select` | Projection | Optional — omitted means `select *`. `*`, field/expression list, typed object/array constructors, aggregates; optional `distinct`. |
| `from` | Source | One collection (or `@all_docs`) with optional alias. |
| `load` | Reference resolution | Binds a referenced document id at a path to an alias usable in later clauses. |
| `where` | Filter | Boolean expression over paths, parameters, literals, registry functions. |
| `group by` | Grouping | Keys with optional aliases; enables aggregates in `select`/`order by`. |
| `order by` | Ordering | Expressions with `asc`/`desc`, `nulls first`/`last`; `score()` and `spatial.distance()` gated. |
| `limit` | Page size | `n >= 1`, or a parameter bound to such a value. `limit 0` fails closed. |
| `offset` | Page skip | `n >= 0`, or a parameter. A separate clause — TQL has no comma form. |
| `include` | Eager fetch | Paths whose referenced documents ship alongside results. |
| parameters | Binding | Out-of-band, typed, named; referenced as `$name` (Section 4.9). |

### 4.1 `from`

```text
from Orders as o
from "Orders/Archived"      -- quoted collection name
from @all_docs              -- every collection (with @collection filter)
```

`from` targets a **collection**, never an index. The alias is
optional and always introduced by `as` (unlike RQL, where `as` is
optional — recorded in the mapping). Unaliased queries address the
document root directly (`name`, `address.city`).

### 4.2 `load`

```text
from orders as o load o.customerId as c, o.warehouseId as w
```

Each item reads a string-valued path as a document id and binds the
alias to the referenced document (or to null when absent). Loaded
aliases are addressable in `where`, `group by`, `order by`, and
`select`. Resolving a reference is not a relational join: it is
bounded to id lookup.

### 4.3 `where`

A boolean expression (Section 7). Array predicates use any-element
semantics by default; `all in` quantifies over listed values
(Section 6.2).

### 4.4 `group by`

```text
group by c.region as region
```

Group keys are expressions with optional aliases (default key name is
the last member of the key's path). With `group by` present, every
non-aggregate expression in `select` and `order by` must be
functionally derived from the group keys (static check, fail closed).
Aggregates without `group by` are whole-query aggregates producing a
single row.

### 4.5 `select`

Four forms:

1. `select *` — the stored document (metadata is addressable but not
   merged into the body).
2. Field/expression list — `select name, address.city as city`.
   Without an alias the output key is the last member of the path;
   explicit `as` is recommended whenever keys could collide.
3. Typed object constructor — `select { … }` with expression members
   (Section 7.3). This is the typed replacement for RQL's JavaScript
   projections.
4. Aggregate list — with or without `group by` (Section 7.4).

`select distinct` deduplicates projected rows by structural value
equality after projection and before paging.

### 4.6 `order by`

```text
order by o.placedAt desc nulls last, c.region
```

- Direction: `asc`/`ascending` (default) or `desc`/`descending`.
- `nulls first`/`nulls last` overrides the engine default (the W3-2
  total document order sorts missing and null first).
- Multiple keys apply in order; ties keep the stable engine order.
- TQL carries no `as string|long|double|alphaNumeric` type hints:
  ordering follows the engine's total document order. Recorded as an
  accepted difference in the mapping.
- `score()` requires the search capability; `spatial.distance(a, b)`
  requires the spatial capability (Section 8). `random()` does not
  exist (nondeterministic ordering would break replayable live
  results and vectors).

### 4.7 `limit` and `offset`

```text
limit 20 offset 40
limit $pageSize
```

Bounds are `limit >= 1` and `offset >= 0`, validated at parse time
for literals and at parameter-binding time for parameters. RQL's
`limit x, y` comma form and `filter_limit` are not in TQL (mapping:
translated and accepted-difference respectively).

### 4.8 `include`

```text
include o.customerId, o.warehouseId
```

Declarative eager fetch: referenced documents ship alongside the
result set without becoming addressable in the query (contrast
`load`). Family-typed include arguments (counters, time series) are
not in tql/1.0; they follow the family parity rows (PB-3010,
PB-3020).

### 4.9 Parameters

Parameters are **named, typed, and out-of-band**. The TQL text
references them as `$name`; the typed TQL operation carries their
values alongside the grammar version and the text. The studio-style
"declare parameters before the query" prefix and the JSON parameter
body of V1 both translate to this single mechanism.

Fail-closed binding rules:

- Every `$name` in the text must have a binding: otherwise
  `UnboundParameter`.
- Every binding must be referenced: otherwise `UnexpectedParameter`.
  (Strict pairing keeps stored queries, MCP tools, and golden vectors
  self-describing.)
- Parameter values are data, never parsed text: a parameter can
  never inject clauses or change the parse tree.

## 5. Grammar versioning and fail-closed rules

### 5.1 Grammar version identity

The grammar version is the pair `(major, minor)`, written `tql/1.0`.
It identifies the grammar and the function registry together.

### 5.2 Version in every parsed artifact

Every parsed TQL artifact carries the grammar version:

- the parse input — a typed `(grammar_version, text, parameters)`
  triple; the engine seam requires the version explicitly and never
  guesses (client surfaces fill in their compiled-in version);
- the AST root node of every parsed query;
- every serialized AST artifact: TBEP frames, persisted/stored
  queries and subscription definitions, golden vector entries, and
  MCP operation payloads;
- every parse or plan error payload, so a failure is attributable to
  a version.

An artifact without a version is rejected with `MissingGrammarVersion`.

### 5.3 Fail-closed version rules

- A version the parser does not implement — unknown major, unknown
  minor, malformed — is `UnsupportedGrammarVersion`. There is no
  "best effort" parse of a newer or older artifact.
- Evolution policy: purely additive changes (new clause option, new
  registry function) bump the minor; anything removed, retokenized,
  or semantically changed bumps the major. A parser may accept older
  minors of its own major (the additive rule makes them prefixes of
  the current grammar) but must reject newer minors and every other
  major.

### 5.4 Typed error vocabulary (required classes)

TQL-02 owns the concrete error type; this spec fixes the classes and
their meaning. Every error carries the grammar version, a source
position (byte offset, line, column) where applicable, and the
offending construct.

| Class | Meaning |
| --- | --- |
| `MissingGrammarVersion` | Artifact carries no version. |
| `UnsupportedGrammarVersion` | Version not implemented by this parser. |
| `ParseError` | Text does not match the grammar for the declared version. |
| `UnknownFunction` | Call names no registry function for the declared version. |
| `InvalidArgument` | Static arity or argument-type mismatch in a registry call. |
| `UnboundParameter` / `UnexpectedParameter` | Parameter pairing failure. |
| `CapabilityUnavailable` | Registry function exists but the engine does not ship the capability (Section 8). |
| `InvalidBound` | Paging bound violation (`limit 0`, negative offset). |
| `InvalidIdentifier` | Identifier fails the shared identifier discipline. |
| `ReservedName` | Alias/collection takes a reserved name (`vector`, `spatial`). |

No error class degrades to a warning; no query executes partially.

## 6. Document-native constructs

### 6.1 Nested paths

Dot paths address arbitrarily deep objects: `address.city`,
`shipment.history[0].warehouse`. A path segment that is absent or
reaches a non-object evaluates to **missing**, which predicates treat
as false and `select` omits.

### 6.2 Array navigation

Two bracket forms navigate arrays:

- `items[2].price` — positional (zero-based). Out-of-range indexes
  are missing.
- `items[].qty` — explicit **any-element** navigation.

**Any-match semantics:** a predicate over an array field is true when
**any** element satisfies it — for `items[].qty > 5` and for a bare
array field `tags = 'red'` alike (V1 semantics preserved). In
projections, `items[].sku` collects the matching members into an
array in document order.

`all in` quantifies the other way: `tags all in ('a', 'b')` is true
when the array contains every listed value.

### 6.3 Metadata access

Engine metadata is addressable through pseudo-fields and one function:

| Form | Content |
| --- | --- |
| `@id` | Document identity; equal to `id()`. |
| `@collection` | Owning collection name. |
| `@last_modified` | Last modification timestamp (comparable, orderable). |
| `@change_vector` | Change vector string. |
| `@metadata.<member>` | Remaining engine metadata members (object root). |
| `id()` / `id(alias)` | Document identity of the source (or of a `load` alias). |

Metadata is engine-owned and read-only; it never merges into the
document body.

### 6.4 Dynamic fields

Fields with names a bare identifier cannot express are quoted path
segments:

```text
where "properties"."Screen Size" between 6 and 7
select props."Color (front)" as color
```

Dynamic fields keep the W3-2 discipline: any name the document model
accepts is addressable; nothing about TQL narrows legal field names.

## 7. Typed expression and projection system

### 7.1 Expression grammar

Expressions are the typed core used by `where`, `select`, `order by`,
`group by`, and function arguments. Precedence, lowest to highest:
`or` < `and` < `not` < comparisons (`=`, `!=`, `<>`, `<`, `<=`, `>`,
`>=`, `between`, `in`, `all in`) < `+`/`-` < `*`/`/`/`%` < unary sign
< primaries (literals, `$parameters`, paths, calls, constructors,
parentheses). The EBNF in Section 10 is normative.

### 7.2 Typing discipline

Expression value types are `null`, `boolean`, `number` (the engine's
unified exact numeric domain — Integer and Number compare exactly per
the W3-2 order), `string`, `array`, `object`, `document`. The parser
performs **static** checks — arity, argument types, aggregate
placement, alias resolution, parameter pairing — and fails closed on
violation. At evaluation:

- Comparisons are defined within the numeric, string, and boolean
  domains only; any other pair, a cross-type pair, a `NaN` operand,
  or a missing field is **false, not an error** (W3-2 semantics).
- `=` matches a present value structurally: present `null` and a
  missing field are distinct.
- Arithmetic on non-numbers is a static type error where detectable;
  otherwise false/missing at evaluation — never a runtime crash.

### 7.3 Constructors (the projection system)

Projections build documents with typed constructors instead of code:

```text
select {
  id: id(),
  buyer: c.name,
  lines: { sku: lines[].sku, qty: lines[].qty },
  stage: if(shipment = null, 'pending', 'shipped'),
  contact: coalesce(c.email, c.phone)
}
from orders as o load o.customerId as c
```

Object constructors `{ key: expr, … }` and array constructors
`[expr, …]` compose any registry expressions. This is the flagship
replacement for RQL's JavaScript projections: the same shaping power,
expressed as data, with no execution.

### 7.4 Aggregates

`count()`, `count(expr)`, `sum(expr)`, `avg(expr)`, `min(expr)`,
`max(expr)`. Aggregates work at two statically distinguished scopes:

- **Per-document reduction.** When the aggregated expression is
  multi-valued within one document — an array field or any `[]`
  any-element navigation — the aggregate reduces that array inside
  the current document and is legal anywhere an expression is:
  `select { total: sum(lines[].price * lines[].qty) } from orders`.
  This is the typed replacement for the per-document arithmetic RQL
  users wrote in JavaScript projections.
- **Group or whole-query reduction.** When the aggregated expression
  is single-valued per document — `sum(o.total)` — the aggregate
  reduces across rows: per group under `group by`, or to one row for
  the whole query without it. These are legal in `select` and
  `order by` only; in `where` they are a static error. With
  `group by`, every non-aggregate expression in `select` and
  `order by` must be functionally derived from the group keys.

`count()` (no argument) counts rows: group rows under `group by`,
all rows without it. `count(expr)` counts matching elements
(per-document scope) or non-missing evaluations (group scope). The
scope rule is a static type judgment on the argument — multi-valued
argument means per-document; it never depends on the data.

### 7.5 Function registry (tql/1.0)

The registry is closed and part of the grammar version. Calls outside
it are `UnknownFunction`. Names use snake_case (RQL's camelCase maps
in translation).

**Always available — scalar:**

| Function | Type |
| --- | --- |
| `id([alias])` | `-> string` document identity |
| `len(x)` | `-> number` — string length, array length, object member count |
| `lower(s)`, `upper(s)` | `-> string` |
| `concat(a, b, …)` | `-> string` |
| `contains(s, sub)`, `starts_with(s, prefix)`, `ends_with(s, suffix)` | `-> boolean` |
| `if(cond, then, else)` | `cond ? then : else`, any matching pair |
| `coalesce(a, b, …)` | first non-null argument |
| `exists(field)`, `missing(field)` | `-> boolean` presence tests |

**Aggregate:** Section 7.4.

**Predicate modifier:** `exact(comparison)` — requires exact
(non-analyzed) matching for the wrapped comparison.

**Capability-gated:** `search`, `boost`, `score`, `vector.search`,
`vector.of`, and the `spatial.*` family — Section 8.

## 8. Capability-gated predicates: search, vector, spatial

These predicates are **in the grammar** so text, vectors, tools, and
stored queries are stable — but each is **gated on the capability the
engine actually ships**. A gated call against an engine that does not
report the capability fails closed with `CapabilityUnavailable`;
it never degrades to a different predicate or a no-op. Vector search
is CP-01's required competitive row; its TQL form is fixed here so
the capability and the language land together.

### 8.1 Full-text search (gated)

```text
where search(body, $terms)          -- terms combined with or by default
where search(body, $terms, and)     -- explicit combiner
where boost(search(title, $terms), 3)
order by score() desc
```

`search(field, term [, and|or]) -> boolean`; `boost(predicate, weight)
-> boolean`; `score() -> number` (ranking, usable in `order by` and
`select`).

### 8.2 Vector search (gated)

```text
where vector.search(embedding, $queryVector, 0.62)
order by score() desc limit 25
```

`vector.search(field, vector [, threshold]) -> boolean`. The vector
operand is a typed `$parameter` (the recommended form: vectors are
data, not text) or the constructor `vector.of(number, …)` for small
inline vectors. RQL's `embedding.text(...)`-style in-query embedding
generation is not carried over: embedding production is an engine /
Platform capability, and queries bind its output by parameter — the
query text never depends on an AI task.

### 8.3 Spatial (gated)

```text
where spatial.within(loc, spatial.circle(10, 59.9, 10.7, 'km'))
where spatial.intersects(area, spatial.wkt($wkt))
order by spatial.distance(loc, spatial.point(59.9, 10.7))
```

Predicates: `spatial.within`, `spatial.contains`,
`spatial.disjoint`, `spatial.intersects` — each
`(field, shape) -> boolean`. Shapes: `spatial.circle(radius, lat, lng,
units)`, `spatial.point(lat, lng)`, `spatial.wkt(text)`.
`spatial.distance(a, b) -> number`, usable in `order by` and
`select`.

## 9. Composition: live results and MCP

- **Subscriptions.** A TQL query is a deterministic, replayable
  definition: same grammar version, same text, same parameters, same
  result stream over the same data state. Live results (TQL-03 with
  the SE wave) are defined as change-driven recomputation of a TQL
  query, carrying the grammar version in the subscription artifact.
  This is why the language excludes `random()` and `now()`.
- **MCP.** TQL is MCP-exposable as **one typed operation** —
  `(grammar_version, text, parameters)` in, typed rows or typed
  errors out — under the workspace invariant's authorization,
  tenant-isolation, confirmation, and audit rules. There is no raw
  query-string surface and no generic escape hatch beside the typed
  operation.

## 10. EBNF grammar (normative, tql/1.0)

ISO EBNF. Terminals are quoted; keywords are case-insensitive;
comments (`//`, `/* */`) and whitespace separate tokens and are not
 productions. The grammar accepts any call syntactically; the
 registry and static checks (Section 7, Section 5.4) reject unknown
 names and invalid usage after parsing.

```ebnf
tql_query        = [ select_clause ] , from_clause , [ load_clause ] ,
                   [ where_clause ] , [ group_clause ] , [ order_clause ] ,
                   [ limit_clause ] , [ offset_clause ] , [ include_clause ] ;

select_clause    = "select" , [ "distinct" ] , projection_list ;
projection_list  = projection_item , { "," , projection_item } ;
projection_item  = "*" | expression , [ "as" , alias ] ;

from_clause      = "from" , source , [ "as" , alias ] ;
source           = collection_name | all_docs_token ;
all_docs_token   = "@all_docs" ;
collection_name  = identifier | quoted_identifier ;

load_clause      = "load" , load_item , { "," , load_item } ;
load_item        = path , "as" , alias ;

where_clause     = "where" , expression ;

group_clause     = "group" , "by" , group_key , { "," , group_key } ;
group_key        = expression , [ "as" , alias ] ;

order_clause     = "order" , "by" , order_key , { "," , order_key } ;
order_key        = expression , [ order_direction ] , [ nulls_order ] ;
order_direction  = "asc" | "ascending" | "desc" | "descending" ;
nulls_order      = "nulls" , ( "first" | "last" ) ;

limit_clause     = "limit" , page_bound ;
offset_clause    = "offset" , page_bound ;
page_bound       = unsigned_integer | parameter ;  (* limit >= 1, offset >= 0 *)

include_clause   = "include" , path , { "," , path } ;

alias            = identifier | quoted_identifier ;

(* Expressions, lowest to highest precedence *)
expression       = or_expression ;
or_expression    = and_expression , { "or" , and_expression } ;
and_expression   = not_expression , { "and" , not_expression } ;
not_expression   = "not" , not_expression | comparison ;
comparison       = additive ,
                   [ comparison_operator , additive
                   | "between" , additive , "and" , additive
                   | ( "in" | all_in ) , "(" , [ argument_list ] , ")" ] ;
comparison_operator = "=" | "!=" | "<>" | "<" | "<=" | ">" | ">=" ;
all_in           = "all" , "in" ;

additive         = multiplicative , { ( "+" | "-" ) , multiplicative } ;
multiplicative   = unary , { ( "*" | "/" | "%" ) , unary } ;
unary            = ( "-" | "+" ) , unary | primary ;

primary          = literal | parameter | path | function_call
                 | object_constructor | array_constructor
                 | "(" , expression , ")" ;

literal          = string_literal | number_literal
                 | boolean_literal | null_literal ;
string_literal   = "'" , { string_character | "''" } , "'" ;
string_character = ? any character except "'" and control characters ? ;
number_literal   = unsigned_integer ,
                   [ "." , digit , { digit } ] ,
                   [ "e" , [ "+" | "-" ] , digit , { digit } ] ;
unsigned_integer = digit , { digit } ;
boolean_literal  = "true" | "false" ;
null_literal     = "null" ;

parameter        = "$" , parameter_name ;
parameter_name   = identifier ;

path             = path_root , { path_segment } ;
path_root        = member_name | metadata_field ;
metadata_field   = "@id" | "@collection" | "@last_modified"
                 | "@change_vector" | "@metadata" ;
path_segment     = "." , member_name | array_index | array_any ;
member_name      = identifier | quoted_identifier ;
array_index      = "[" , unsigned_integer , "]" ;
array_any        = "[" , "]" ;

function_call    = function_name , "(" , [ argument_list ] , ")" ;
function_name    = identifier , { "." , identifier } ;  (* e.g. vector.search *)
argument_list    = expression , { "," , expression } ;

object_constructor = "{" , [ object_member , { "," , object_member } ] , "}" ;
object_member    = member_name , ":" , expression ;
array_constructor = "[" , [ argument_list ] , "]" ;

identifier       = identifier_first_or_inner , { identifier_char } ;
identifier_first_or_inner = letter | digit | "_" ;
identifier_char  = letter | digit | "_" | "-" ;
quoted_identifier = '"' , { quoted_character | '""' } , '"' ;
quoted_character = ? any character except '"' and control characters ? ;
letter           = "A" … "Z" | "a" … "z" ;
digit            = "0" … "9" ;
```

Lexical notes (normative):

- `identifier` must not match any reserved word (Section 3.3) and
  must not begin with `-` — such text lexes as a negative literal and
  then fails to parse as a name, which is the fail-closed outcome.
- `between x and y` binds its inner `and` first; a parenthesized
  `( expression )` primary is the escape hatch.
- `array_index`/`array_any` continue a path and follow a `member_name`
  or another bracket segment; a `[` in primary position is an
  `array_constructor`. A function call is not indexable in tql/1.0.
- `identifier` also serves `parameter_name`, `alias`, and
  `collection_name`; all reject reserved words in bare form.
- `*` is a projection only inside `projection_item`; in expression
  position it is the multiplication operator.

## 11. AST node inventory

The parser produces a typed AST whose every node carries the grammar
version and source span. Node kinds (descriptive; TQL-02 owns the
Rust types):

- `Query { grammar_version, projection, source, loads, filter,
  grouping, ordering, paging, includes }`
- `Source { collection | all_docs, alias? }`
- `Projection { WholeDocument | Items(Vec<ProjectionItem>) ,
  distinct }` — `ProjectionItem { expr, alias? }`; an omitted
  `select` parses as the whole-document projection
- Expressions: `Literal`, `ParameterRef`, `Path { segments:
  Member | ArrayIndex | AnyElement }`, `Call { name, args }`,
  `Unary`, `Binary { arithmetic | comparison }`, `Between`, `In {
  quantifier: Any | All }`, `Logical { and | or }`, `Not`,
  `ObjectCtor`, `ArrayCtor`
- `LoadItem`, `GroupKey`, `OrderKey { expr, direction?, nulls? }`,
- `Paging { limit, offset }` (bounds pre-validated),
- `Include { paths }`

The AST contains **no unvalidated strings**: identifiers,
collection names, and function names are validated at parse;
parameters are stored as typed binding slots.

## 12. Worked examples

### 12.1 Flat documents with parameters

```text
select * from users
where country = $country and active = true
order by @last_modified desc
limit 20 offset 40
```

Parameters: `{ "country": "Norway" }`. Reads: active Norwegian users,
newest first, page 3 of 20.

### 12.2 Nested paths

```text
select name, address.city as city, address.country
from customers
where address.country in ('NO', 'SE', 'DK')
```

Output keys: `name`, `city`, `country`.

### 12.3 Arrays: navigation, any-match, constructors

```text
select {
  id: id(),
  skus: lines[].sku,
  volume: sum(lines[].qty),
  stage: if(shipment = null, 'pending', 'shipped')
}
from orders
where lines[].qty > 10
```

`where` keeps orders with any line of quantity over 10; the
projection collects matching SKUs, sums quantities across lines, and
types a conditional stage without code.

### 12.4 Dynamic fields

```text
select "properties"."Screen Size" as screen
from devices
where "properties"."Screen Size" between 6 and 7
  and props."Color (front)" = $front
```

Quoted segments address any legal stored name, including names with
spaces, punctuation, or reserved words.

### 12.5 Metadata

```text
select @id, @collection from @all_docs
where @collection = 'orders' and @id = $id
```

Or a quick whole-document form, ordered by engine metadata (the
omitted `select` means `select *`):

```text
from audit_events order by @last_modified desc limit 100
```

### 12.6 Load and include

```text
select o.placedAt as placed, c.name as buyer, w.name as warehouse
from orders as o
load o.customerId as c, o.warehouseId as w
where c.region = 'EMEA' and w.active = true
order by o.placedAt desc
include o.customerId
limit 50
```

`load` makes references addressable in `where`/`order by`/`select`;
`include` ships the referenced customer documents alongside results.

### 12.7 Group by with aggregates

```text
select c.region as region, count() as orders, sum(o.total) as revenue
from orders as o
load o.customerId as c
group by c.region
order by revenue desc
limit 10
```

### 12.8 Vector search (capability-gated)

```text
select @id, score()
from products
where vector.search(embedding, $queryVector, 0.62)
order by score() desc
limit 25
```

Until the engine ships vector search, this exact text fails closed
with `CapabilityUnavailable` — the text is stable, the capability is
not assumed.

### 12.9 Full-text search (capability-gated)

```text
select @id, title from articles
where boost(search(body, $terms, and), 3) or search(title, $terms)
order by score() desc
limit 10
```

### 12.10 Spatial (capability-gated)

```text
select @id from venues
where spatial.within(loc, spatial.circle(10, 59.9, 10.7, 'km'))
order by spatial.distance(loc, spatial.point(59.9, 10.7))
limit 5
```

### 12.11 Fail-closed outcomes

| Input | Error |
| --- | --- |
| grammar version `tql/2.0` against a `tql/1.x` parser | `UnsupportedGrammarVersion` |
| parsed artifact with no version field | `MissingGrammarVersion` |
| `where x = $nope` without a binding for `$nope` | `UnboundParameter` |
| extra binding `{"unused": 1}` never referenced | `UnexpectedParameter` |
| `vector.search(...)` without shipped vector capability | `CapabilityUnavailable` |
| `limit 0` | `InvalidBound` |
| `select { a: 1 } where b = 2 from t` (missing `from` position) | `ParseError` |
| `trim(x)` | `UnknownFunction` |
| `where sum(total) > 5` (group-scope aggregate in `where`) | `InvalidArgument` |

## 13. Relationship to the engine seam and the wave

- **TQL-02** implements the zero-dependency, deterministic, bounded
  parser for this grammar, the typed error vocabulary, the golden
  vector corpus, and the lowering from the TQL AST onto the W3-2
  compile seam (`QueryStore::compile`), which gains the nested-path,
  array, metadata, parameter, grouping, load/include, and projection
  forms TQL requires. Field semantics TQL inherits where defined
  (total document order, null/missing distinction, paging bounds)
  stay as the W3-2 record defines them.
- **TQL-03** exposes the typed TQL operation across TBEP, host, CLI,
  studio, MCP, and subscriptions, and records the Platform `tql`
  surface compat decision.
- **TQL-04** turns `docs/tql/rql-mapping.md` into conformance
  vectors and WS-PARITY disposition proposals.
- This specification makes no V1 parity claim and no capability
  claim; those live in the parity matrix with Proof IDs.
