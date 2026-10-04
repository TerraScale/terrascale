# Query v1

`POST /v1/databases/{db}/collections/{collection}/query` with a key holding
`read`. This contract defines the filter grammar shared by queries,
transaction `assert` ops, key row filters and shapes.

## Request

```json
{"filter":{"and":[{"eq":["/org_id","0192…"]},{"gte":["/created_at","1759363200000"]}]},
 "sort":[{"path":"/created_at","order":"desc"}],
 "project":["/email","/name"],
 "limit":50,
 "cursor":null,
 "count":true,
 "explain":"analyze"}
```

All members are optional. `limit` is 1–1000 (default 50). Unknown members fail
with `400 invalid_query`.

## Paths

A path is an RFC 6901 JSON Pointer with 1–16 segments and at most 4 KiB.
Segments are object member names; numeric segments are member names, not array
indices. The pseudo-path `"$id"` is the document ID. Wherever a path is
accepted, `{"lower":"/path"}` applies Unicode default case folding to string
values before comparison.

## Filter grammar

| Form | Meaning |
| --- | --- |
| `{"and":[F,…]}` | 1–64 children, all match |
| `{"or":[F,…]}` | 1–64 children, any matches |
| `{"not":F}` | negation |
| `{"eq":[P,V]}`, `{"ne":[P,V]}` | equal / not equal (`ne` also matches missing) |
| `{"lt":[P,V]}`, `{"lte":…}`, `{"gt":…}`, `{"gte":…}` | ordered comparison |
| `{"in":[P,[V,…]]}`, `{"nin":[P,[V,…]]}` | 1–256 values |
| `{"exists":P}` | the member is present (including `null`) |
| `{"prefix":[P,"str"]}` | string starts with `str` |
| `{"contains":[P,V]}` | the value is an array containing scalar `V` |

`V` is a JSON scalar: string, exact decimal number, boolean or `null`. Ordered
comparisons reject `null`. Nesting depth is at most 8 and a filter has at most
256 nodes.

## Comparison semantics

- Values are ordered by type first: missing < null < false < true < numbers <
  strings < arrays/objects. Ordered operators compare only within the type of
  `V`: `{"gt":["/n",1]}` never matches strings.
- Numbers compare exactly: `1`, `1.0` and `1e0` are equal.
- Strings compare by UTF-8 bytes (binary collation) unless `lower` is used.
- Scalar operators other than `contains`, `exists`, `ne` and `nin` never match
  array or object values.
- `"$id"` compares as its lowercase UUID string.
- TTL-expired documents never match.
- A conflicted document matches when any live sibling matches; it is returned
  with `state:"conflict"` and all siblings.

## Sort and projection

`sort` has at most 4 `{path, order:"asc"|"desc"}` keys, and the result is
always finally ordered by `"$id"` ascending. Missing values sort first in
ascending order; array and object values sort after strings and are ordered by
document ID.

`project` lists at most 64 paths. Projected documents keep only those members,
with their parent objects. Conflicted documents ignore projection.

## Response

```json
{"items":[{"id":"…","version":"tbv1-…","state":"value","document":{"email":"a@b.c"}}],
 "next_cursor":"…","count":{"value":"1234","exact":true},"explain":{…}}
```

`count` is present only when requested. It counts every match of the filter,
ignoring `limit` and `cursor`, exactly up to 100000 examined entries, after which
`exact` is `false` and `value` is a lower bound.

## Pagination

Cursors are keyset positions (the sort key values and document ID of the last
returned item) wrapped in an HMAC envelope bound to the tenant, database,
collection, canonical digest of `filter`, `sort` and `project`, catalog
generation and key ID. `limit` may change between pages; `count` and `explain`
are evaluated only on requests without a cursor and are ignored otherwise. The HMAC key
is stored in the system catalog, so cursors survive restarts and work on any
process for one hour. Each page reads the latest committed state; documents that
do not change between pages are neither repeated nor skipped. A cursor used with
a different query fails with `400 invalid_cursor`.

## Execution

The planner normalizes the filter and chooses a plan:

1. `point`: an `eq` or `in` on `"$id"`.
2. `index_range`: one index chosen by equality prefix, then sort, then range
   (ESR), with any remaining predicates applied as a residual filter.
3. `index_union`: an `or` whose every branch has an index range, merged and
   deduplicated (no sort from the index).
4. `collection_scan`: a bounded scan of at most 4096 documents per page. A page
   may contain fewer than `limit` items, or none, and still return
   `next_cursor`.

The complete filter is always re-evaluated on every fetched document, so an
index only generates candidates. A sort without a matching index is performed
in memory only when at most 10000 documents are examined; otherwise the query
fails with `400 sort_requires_index` and `"suggested_index":{"keys":[…]}`. Every
query's normalized shape is recorded for the workload advisor
([auto-index.md](auto-index.md)).

## Explain

`"explain":"plan"` returns the plan without executing it (`items` is empty).
`"explain":"analyze"` executes the query and adds actual counters. Fields that
the engine cannot measure are omitted rather than estimated.

```json
{"plan":{"kind":"index_range","index":"by_org_created","bounds":[…],"residual":true,"sort":"index"},
 "estimated_rows":"40",
 "actual":{"rows":"38","index_entries_scanned":"40","documents_read":"40",
           "bytes_read":"…","memory_bytes":"…","duration_us":"…"}}
```
