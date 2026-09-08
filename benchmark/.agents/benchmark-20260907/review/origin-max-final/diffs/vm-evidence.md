# Origin sequence MAX allocation plan evidence

Source cohort: `/tmp/terrabase-qual-fae32` (frozen; no edits). The reproducible
probe in `probe.py` declares the exact allocation table columns and indexes from
the frozen `SCHEMA`: `documents` is `WITHOUT ROWID` with
`documents_origin_frontier`, while `origin_prefixes` and
`replication_publications` are `WITHOUT ROWID` with their declared primary
keys. The query uses the same `origin` and `tenant` parameters for both forms.

| Query | `EXPLAIN QUERY PLAN` branch access | Static opcodes | Runtime VM steps* | Result |
| --- | --- | ---: | ---: |
| Existing outer `MAX` over `UNION ALL` rows | documents: covering index search; origin_prefixes: primary-key search; replication_publications: primary-key search | 52 | 18,054 / 1,800,054 | 2,001 / 200,001 |
| Scalar `MAX` in each of the three branches | documents: covering index search; origin_prefixes: primary-key search; replication_publications: primary-key search | 69 | 86 / 86 | 2,001 / 200,001 |

Both forms preserve empty-branch behavior (`1` for no rows) and tenant
isolation (`1` when the matching tenant has no rows), and a separate origin in
the same tenant returns its own maximum (`1,000,000`). Runtime VM steps are one
single query on each throwaway 2,000-row and 200,000-row fixture measured with
Python's SQLite progress handler; they are plan evidence, not the terrabase
benchmark result. The complete stdout is retained in `probe-output.txt`.
The scalar branch form lets each branch aggregate at its source and is the
proposed measured-cohort patch.

Command used:

```text
python3 .agents/benchmark-20260907/origin-max/probe.py
```

The throwaway database was removed after the read-only plan comparison.
