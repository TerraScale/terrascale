# Comparison-local parameter affinity

SQL comparisons apply the declared column's affinity independently to each literal or bound parameter occurrence.
For `t(n INTEGER, s TEXT)` containing `(1, '01')`, this returns one row with `['01', 'text']`:

```sql
SELECT :x, typeof(:x) FROM t WHERE n = :x AND s = :x;
-- bind x as TEXT '01'
```

The numeric comparison uses `1`; the text comparison and projection retain `'01'`. Reversed comparisons, range
comparisons, `IN` lists and `BETWEEN` bounds use the same rule. Parentheses preserve a column's affinity. NULL and nonnumeric text retain their existing storage classes when
numeric affinity cannot convert them. Untyped columns do not coerce their operands.

Identifier lookup, CTE identity, join column visibility and output-name deduplication fold ASCII A–Z only,
matching SQLite. Non-ASCII identifiers such as `é` and `É` remain distinct, including quoted CTE names.
Column lookup respects SELECT scopes and table aliases, including same-named columns with different declared types
in joins and nested queries. Direct column projections, `SELECT *` and qualified wildcards through plain SELECT CTEs and derived tables
retain their source affinity; computed expressions do not inherit affinity merely from their output names.
Projection aliases are resolved in the clauses where SQLite permits them, with real columns taking precedence in
`WHERE` and other expressions. A bare `ORDER BY` alias refers to the projection; inside an `ORDER BY`
expression real columns take precedence. Correlated direct projections retain outer-column affinity, and a local
qualifier without a matching column does not hide a matching outer column. Duplicate CTE column names are disambiguated after
applying the CTE's explicit column aliases (`v`, `v:1`, …).
`USING` and `NATURAL` joins expose merged unqualified columns and omit their right-hand duplicates in `SELECT *`;
qualified wildcards retain every column of the named source. Compound SELECT outputs retain an affinity only when
all branches agree, including no affinity for computed expressions. A comparison against a compound output with
conflicting branch affinities fails with `SQLITE_ERROR` and `compound query output affinity is ambiguous`; ordinary
result reads remain available. Unresolved compound branch shapes fail with `compound query output shape cannot be inferred`.
VALUES sources expose `column1`, `column2`, … with no affinity; they do not erase known columns in other sources.
Unresolved source shapes fail comparisons with `SQLITE_ERROR` and `SQL source column affinity cannot be inferred`. Every equality/order comparison, `IS [NOT] DISTINCT FROM`, `IN` list, `IN (SELECT …)`, `BETWEEN`, and simple
`CASE … WHEN` comparison is inspected. Signed numeric literals and booleans can be converted independently.
Numeric comparisons apply NUMERIC affinity, preserving integer precision even when the source column is REAL;
numeric text accepts SQLite ASCII whitespace. The engine formats TEXT conversions, including REAL values.
CAST expressions carry their declared target affinity; COLLATE and scalar subqueries retain their source affinity.
Unary plus, arithmetic, functions and CASE results have no declared affinity.

When SQLite needs conversion of a row-dependent operand, preparation explicitly fails with `SQLITE_ERROR` and
`comparison requires unsupported row-dependent affinity conversion`. This includes column-to-column comparisons
with incompatible affinities, dynamic `IN` entries, incompatible `USING`/`NATURAL` join keys, and BETWEEN/simple CASE
forms that need different conversions of a shared operand. Comparisons between columns with the same affinity or
numeric affinities remain available. Correlated CTE outputs without a self-contained column shape are refused with
`SQL source column affinity cannot be inferred`; their inferred shape is never reused across differing outer scopes. Row-value comparisons fail with `row-value comparison affinity is not supported`.
Short `IS :parameter` and `IS NOT :parameter` syntax uses the same affinity rules as
`IS NOT DISTINCT FROM` and `IS DISTINCT FROM`; NULL/boolean RHS predicates retain SQLite truth-test semantics.
Aliased parenthesized join groups are refused with `SQLITE_ERROR` and
`affinity for aliased parenthesized joins is not supported`. These explicit errors bound the supported SQL surface;
this change does not claim full SQLite expression compatibility.

Preparation reserves 2 MiB from the foreground memory budget before copying or resolving the query. Inference is
memoized per execution before descending into CTEs, with at most 2,000 columns per expanded shape, 1 MiB of charged
metadata, 128 registered CTE identities, 128 query outputs, 128 set-expression bodies, 256 FROM groups, 4,096 visited AST objects and 256 AST traversal levels. Expansion beyond
these bounds fails with `SQLITE_TOOBIG` and `SQL column metadata exceeds the preparation budget`, before engine execution;
failed memory admission returns `SQLITE_NOMEM`. Repeated references share inferred shapes rather than expanding recursively.

This corrects read results shared by HTTP SQL, Hrana/libSQL and PostgreSQL adapters. Request/response schemas,
public parameter numbering, stored documents and the storage format are unchanged; no migration is needed.
Each execution copies the validated query and creates internal comparison-specific numeric bound slots.
TEXT comparisons cast their literal/parameter occurrence in the engine; original parameter slots remain unchanged. Cached statements
and transaction replay retain their original placeholders. Parameter values are never interpolated into SQL.

Fast listener regressions: `SqlAffinityTests` covers HTTP batches with changing argument types and values;
`HranaSqlRegressionTests` covers cross-request transaction replay with the same cached read and different arguments.
