# Comparison-local affinity and value IS comparisons

Each comparison converts its literal or parameter occurrence independently. Original bound values remain in
projections, cached statements and transaction replay. INTEGER `n=1`, TEXT `s='01'` and TEXT `:x='01'` satisfy
both `n=:x AND s=:x` and `n IS :x AND s IS :x`. IS is NULL-safe; boolean RHS predicates retain SQLite truth tests.
Identifier lookup folds ASCII only and preserves SELECT scopes, aliases and direct output lineage.

Conversions of row-dependent values with incompatible affinities fail with `SQLITE_ERROR`, as do ambiguous compound
affinities, unresolved shapes, row-value comparisons and aliased parenthesized joins. Computed/unary-plus expressions
retain no declared affinity. Preparation reserves 2 MiB and enforces column, CTE, traversal and metadata bounds.

The IS parser and complete affinity resolver activate together. Query copies, read round trips, write selections and
guards use `SqlDialect`; input sentinel and complete-consumption checks remain enforced. No API or storage format changes.
