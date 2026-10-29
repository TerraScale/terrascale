# SQLite value IS comparisons

`a IS b` is NULL-safe equality; `a IS NOT b` is NULL-safe inequality. Neither returns NULL.
Two NULLs compare equal; a NULL and a non-NULL value compare unequal. Without column affinity,
`'2' IS 2` is false, while `2 IS 2.0` is true. Bound parameters remain values: binding `true`
as integer 1 does not turn `2 IS :value` into the special truth test `2 IS TRUE`.

SQLite's boolean RHS predicates retain their distinct meaning: `2 IS TRUE`, `0 IS FALSE`,
and `2 IS (TRUE)` are true; `2 IS NOT TRUE` and `NULL IS FALSE` are false. Parentheses retain
this interpretation; unary plus does not (`2 IS +TRUE` is false). The complete RHS is parsed
before recognizing these predicates, so `2 IS TRUE + 1` compares against numeric 2.
`IS [NOT] NULL` and `IS [NOT] DISTINCT FROM` remain accepted, including boolean RHS predicates.

The dialect consumes parser tokens, builds `IsNotDistinctFrom`/`IsDistinctFrom` for general
operands, and retains dedicated NULL/boolean nodes. It does not rewrite client SQL text.
IS, equality, BETWEEN, IN and LIKE share SQLite's left-associative precedence. Ordering
comparisons bind more tightly, followed by bitwise operators, arithmetic, and concatenation.
AND, OR and unary NOT bind less tightly. The same dialect reparses rendered read programs,
write-selection queries and guard expressions; sequence execution renders and reparses the
same nodes. For example, `NULL IS 0 = 0` is true and `0 IS 0 AND 0` is false.

## Comparison-local affinity

Value IS comparisons are activated together with the complete comparison-local affinity resolver.
INTEGER `n`, TEXT `s` and TEXT parameter `'01'` satisfy `n IS :x AND s IS :x` independently,
while projections and transaction replay retain the original parameter value. Cached query copies,
rendered reads, write-selection queries, guards and catalog expressions use the same `SqlDialect`.
The input sentinel check and complete-consumption checks remain at the client boundary.
The affinity resolver's explicit refusal of unsupported row-dependent conversions still applies.

No request/response schema, parameter numbering, stored document or storage format changes;
no migration is needed. HTTP Fast regressions cover 34 SQLite expression controls, aliases,
NULL/text/numeric/boolean parameters, column affinity and malformed inputs. Hrana v2/v3
regressions cover the same expression matrix, describe, stored SQL, sequence UPDATE predicates,
and cross-request transaction replay with changing parameter types and exact returned rows.
