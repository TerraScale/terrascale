# TQL by example — what every query must look like

This is the reference for the shape of TQL (`tql/1.0`) at launch. Every
example here is a case in the query conformance corpus
(`tests/query/tql_golden_vectors.rs`); the parser, the SQLite lowering, the
SDK query builder, and the docs site must all agree with this file. ADR 0009
holds the principles; this file holds the concrete surface. Where an ADR's
first-release scope and this file disagree (FTS, vector, time travel), this
file and `GOAL.md` D-39 win.

Conventions used below: `$name` is a parameter, `→` shows the SDK builder that
lowers to the same canonical AST, and `-- ` starts a comment (comments are
not part of TQL).

---

## 1. Shape

```text
[select [distinct] <projection>]
from <collection> [as of $ts]
[include <relationship>[, ...]]
[where <predicate>]
[group by <expr>[, ...]]
[order by <expr> [asc|desc] [nulls first|last][, ...]]
[limit <n>] [offset <n>]
```

- Clause order is fixed. Omitting `select` returns whole documents.
- Keywords are case-insensitive; canonical form is lowercase. Identifiers
  are case-sensitive. Collection names that are not plain identifiers are
  double-quoted: `from "Orders/Archived"`.
- Field paths use dots: `customer.address.city`. Array elements are never
  addressed by index; use membership predicates.
- Metadata is prefixed with `@`: `@id`, `@collection`, `@revision`,
  `@last_modified`. Nothing else about storage or replication leaks into TQL.
- Literals: `'string'` (escape a quote by doubling it), `42`, `4.2`, `true`,
  `false`, `null`. No date literal: time is always a parameter.
- **Parameters travel out of band.** `$name` in text, typed values in the
  request. There is no string interpolation, ever. The SDK builder only
  produces parameters.
- Every query is bounded: `limit` defaults to the plan's page size (100) and
  is capped by the plan; the server never runs an unbounded scan.
- The same text is a one-shot query or a live query. There is no separate
  subscription language (`GOAL.md` D-48).

---

## 2. Basics

```text
from todos
→ db.todos.query()

from todos limit 20
→ db.todos.query().limit(20)

select title, done from todos
→ db.todos.query().select("title", "done")

select distinct status from orders
→ db.orders.query().select("status").distinct()

from orders where @id = $id
→ db.orders.get(id)           -- get is sugar for this shape
```

## 3. Filters

```text
from orders where status = 'paid'
→ db.orders.query().where("status", "=", "paid")

from orders where status = $status and total >= $min
→ db.orders.query().where("status", "=", status).where("total", ">=", min)

from orders where status in ('paid', 'shipped')
→ .where("status", "in", ["paid", "shipped"])

from orders where total between $lo and $hi
→ .where("total", "between", [lo, hi])

from orders where not (status = 'draft' or total = 0)
→ .where(not(or(eq("status","draft"), eq("total", 0))))

from orders where customer.address.country = 'BR'
→ .where("customer.address.country", "=", "BR")

from products where 'blue' in tags            -- array membership
→ .where("tags", "contains", "blue")

from products where tags all in ($required)   -- array superset
→ .where("tags", "containsAll", required)

from users where exists(phone) and missing(deleted_at)
→ .where(exists("phone")).where(missing("deleted_at"))

from users where starts_with(lower(email), $prefix)
→ .where(startsWith(lower("email"), prefix))
```

Comparison semantics are versioned (`tql/1.0`): strings compare by Unicode
code point, numbers compare numerically across int/float, `null` is never
equal to anything (use `missing()`), and mixed-type comparisons are `false`.

## 4. Ordering and paging

```text
from orders order by created_at desc
→ .orderBy("created_at", "desc")

from orders order by total desc, created_at asc limit 50
→ .orderBy("total", "desc").orderBy("created_at").limit(50)

from orders order by shipped_at desc nulls last limit 20
→ .orderBy("shipped_at", "desc", { nulls: "last" })

from orders order by created_at desc limit 20 offset 40
→ .orderBy("created_at", "desc").limit(20).offset(40)
```

`@id` is always appended as the final tie-breaker, so ordering is total and
paging is stable. Offset paging is fine for small pages; for anything else
the response carries an opaque `cursor` when more rows exist, and the client
resends the **same text and parameters** with `cursor` set. The cursor is a
keyset over the `order by` columns + `@id`; TQL text never changes to page.

```text
→ const page1 = await db.orders.query().orderBy("created_at","desc").limit(100).execute();
→ const page2 = await page1.next();          -- same query, cursor supplied by the SDK
```

## 5. Projections

```text
select title, customer.name as customer from orders
→ .select("title", { customer: "customer.name" })

select { id: @id, total, paid: status = 'paid' } from orders
→ .select({ id: "@id", total: "total", paid: eq("status", "paid") })

select coalesce(nickname, name) as display from users
→ .select({ display: coalesce("nickname", "name") })

select @id, @revision, @last_modified from orders where @id = $id
```

Projection is applied after authorization and before the result leaves the
member: a field the caller may not read is never materialized.

## 6. Aggregates

```text
select count() from orders where status = 'paid'
→ db.orders.query().where("status","=","paid").count()

select status, count() as n, sum(total) as revenue
from orders
group by status
order by revenue desc
→ .groupBy("status").select({ status: "status", n: count(), revenue: sum("total") }).orderBy("revenue","desc")

select customer.country as country, avg(total) as avg_total, max(total) as biggest
from orders
where created_at >= $since
group by customer.country
```

Aggregates are bounded: `group by` is capped at 10,000 groups per query;
exceeding it is a typed `TooManyGroups` error, never a partial result. `sum`
over integers overflows to a typed error, not to a float.

## 7. Full-text search (D-39, FTS5)

```text
from products where search(description, $q)
→ db.products.query().search("description", q)

from products where search(description, $q) and price < $max
order by score() desc limit 10
→ .search("description", q).where("price","<",max).orderBy(score(),"desc").limit(10)

from articles where search(body, $q) or search(title, $q)
order by score() desc
```

`search(field, $q)` is a predicate; `score()` is only valid when the query
contains `search`. The query string uses a small, documented subset of FTS5
syntax (terms, phrases in double quotes, `-term` exclusion, `term*` prefix);
anything else in `$q` is treated as literal text. The first `search` on a
field creates a text auto-index (D-31); until it is active the query returns
`IndexBuilding` with `retry_after`.

## 8. Vector search (D-39, sqlite-vec)

```text
from products where vector.near(embedding, $v, 10)
→ db.products.query().nearVector("embedding", v, 10)

from products where vector.near(embedding, $v, 20) and category = $cat
order by vector.distance(embedding, $v)
limit 5
→ .nearVector("embedding", v, 20).where("category","=",cat).orderBy(vectorDistance("embedding", v)).limit(5)

select @id, name, vector.distance(embedding, $v) as distance
from products
where vector.near(embedding, $v, 10)
```

`vector.near(field, $v, k)` returns the `k` nearest candidates from the ANN
index; other predicates filter that candidate set, so ask for a larger `k`
than `limit` when filtering. `$v` is a typed float array whose dimension
must match the field's declared vector index. Default order in the presence
of `vector.near` is ascending distance.

## 9. Time travel (D-39)

```text
from orders as of $ts where @id = $id
→ db.orders.query().asOf(ts).where("@id","=",id)

select count() from orders as of $ts where status = 'paid'
→ db.orders.query().asOf(ts).where("status","=","paid").count()
```

`as of` reads from a point-in-time restore of the database cached per
`(db, ts)`; it is never reactive (`.subscribe()` on an `as of` query is a
typed `NotReactive` error). `$ts` must be within the plan's backup retention.

## 10. Relationships (ADR 0021)

```text
from orders include customer
→ db.orders.query().include("customer")

from orders include customer, lines.product where status = 'paid'
→ .include("customer", "lines.product")

from customers where exists(orders where status = 'open')     -- reverse, bounded
→ db.customers.query().whereExists("orders", q => q.where("status","=","open"))
```

`include` expands declared relationships (a field holding another document's
`@id`, or an array of them) into the result envelope alongside the root
documents; it never joins ad hoc. Expansion depth is 2 and fanout is capped
by the plan. There are no joins, no arbitrary graph patterns.

## 11. Live queries (D-38, D-48)

The text is identical; only the verb changes.

```text
from orders where status = 'open' order by created_at desc limit 50
→ db.orders.query().where("status","=","open").orderBy("created_at","desc").limit(50).subscribe(view => …)
```

A live query receives a snapshot, then `insert`/`update`/`patch`/`remove`/
`move` deltas as committed rows (local or replicated) change the result;
`reset` when the server chooses to recompute. Everything in sections 2–8 and
10 is reactive; aggregates recompute and emit `update` on the single result
row. `as of` is not reactive.

## 12. Explain

`explain` is a request option, not a keyword. It returns the plan the
optimizer would use for the same text and parameters, without executing.

```text
→ await db.orders.query().where("status","=","paid").orderBy("created_at","desc").limit(20).explain()
```

```json
{
  "grammar": "tql/1.0",
  "collection": "orders",
  "indexes": [
    { "id": "idx_01J…", "kind": "btree", "fields": ["status", "created_at"], "state": "active", "origin": "auto" }
  ],
  "access": "index_range",
  "order": "index",
  "estimated_rows": 1200,
  "bounded_scan": false,
  "reactive": true,
  "dependencies": [{ "collection": "orders", "index": "idx_01J…", "range": ["paid", "paid"] }]
}
```

`indexes[].state` may be `building`, in which case `execute()` on the same
query returns `IndexBuilding { retry_after }` or a `bounded_scan: true`
result when the shape is small enough (D-31). `explain` never names a SQLite
internal.

## 13. Errors

Every error is typed and carries the offending span.

```text
from orders where status =            → ParseError { at: 27, expected: "expression" }
from orders where foo(status)         → UnknownFunction { name: "foo" }
from orders where created_at > now()  → UnknownFunction { name: "now" }   -- time is a parameter
from orders where status = $s         → MissingParameter { name: "s" }     -- when $s is not supplied
from orders limit 100000              → LimitExceeded { max: 1000 }
from venues where spatial.within(...)  → CapabilityUnavailable { name: "spatial.within" }
select score() from products          → ScoreWithoutSearch
from orders as of $ts … .subscribe()  → NotReactive
```

## 14. What TQL is not

- Not SQL. No `join`, no subqueries other than the bounded
  `exists(<relationship> where …)`, no DDL. The SQL that TQL lowers to is an
  implementation detail and never appears in errors or explain output.
  A **separate read-only SQL surface** exists for BI tools (`db.sql(...)`,
  `POST /databases/{id}/sql`, Postgres wire; `GOAL.md` D-54) over the same
  extracted columns; it is not reactive and is not how applications query.
- No user-defined functions, no scripting, no `now()`/`random()`; every
  variable input is a parameter.
- No index hints. The optimizer chooses; the first use of a shape creates
  the index (D-31). Nothing in the text can request a scan.
- No spatial at launch (D-49). `spatial.*` names are reserved and return
  `CapabilityUnavailable`.
- No mutation. Writes go through the document API; TQL only reads.

---

## Appendix: the SDK builder is not a second language

`db.<collection>.query()` produces exactly one canonical AST per chain, and
`Query.toTQL()` prints it in the canonical text form above. The conformance
corpus runs every example both ways (text → AST, builder → AST) and asserts
byte-identical canonical ASTs, parameters, and result digests.
