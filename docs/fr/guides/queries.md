---
title: Queries and pagination
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Queries and pagination

Query JSON documents through your collection handle. The project-scoped Documents API uses JSON filters, sorting and projection; it does not use the older SQL builder.

## Filter a collection

```js
import { v1Filter } from '@terrascale/sdk'

const query = {
  filter: v1Filter.eq('/status', 'active'),
  limit: 20,
}
const page = await posts.query(query, { signal })
```

Here `posts` is a collection handle from the [JavaScript quickstart](/fr/getting-started/javascript), and `signal` is an application deadline's `AbortSignal`.

Paths are RFC 6901 JSON Pointers, such as `/profile/name`; escape `~` as `~0` and `/` inside a member name as `~1`. `$id` refers to document identity. Filter helpers include `eq`, `ne`, `lt`, `lte`, `gt`, `gte`, `in`, `nin`, `exists`, `prefix`, `contains`, `and`, `or` and `not`.

## Follow every cursor

```js
for await (const page of posts.pages(query, { signal })) {
  for (const item of page.items) {
    processDocument(item) // Your application's bounded processing function.
  }
}
```

`pages` follows `nextCursor`, including an empty page with a continuation. `items` iterates documents directly. For manual paging, pass `page.nextCursor` unchanged as `cursor` with the same filter, sort and projection. Stop only when the cursor is `null`. Request `count: true` on the first page only; continuing pages do not recalculate it.

C# uses `TerraBaseQuery.Cursor` and `TerraBaseDocumentPage.NextCursor`; Go uses `Query.Cursor`, `QueryResult.NextCursor` and `Pages`. Go's final cursor is an empty string rather than JavaScript/C#'s `null`.

## Sort and project

```js
const page = await posts.query({
  filter: v1Filter.eq('/status', 'active'),
  sort: [{ path: '/createdAt', order: 'desc' }],
  project: ['/title', '/createdAt'],
  limit: 20,
}, { signal })
```

The current contract allows at most **4 sort keys**, **64 projection paths**, and a page `limit` from **1 to 1000** (default **50**). A page can stop at its byte or scan budget before reaching that count. A sort may require an index and fail with `sort_requires_index`; do not assume automatic index creation. See [limits](/fr/limits). To order by similarity to an embedding, use a [`near` sort](/fr/guides/vector-search).

C# uses `Filter`, `Sort`, `Project`, `Limit`, `Cursor` and `Count` on `TerraBaseQuery`. Filters can be created with `JsonSerializer.SerializeToElement(new { eq = new[] { "/status", "active" } })`.

## Conditional writes and transactions

Read the current version before a dependent update. JavaScript uses `ifMatch: current.version`; C# uses `TerraBasePrecondition.Matches(current.Version)`. A `412 precondition_failed` means the observed version is no longer current. Refetch and resolve the change before writing.

JavaScript's `database.transaction()` and C#'s `TransactAsync` expose database-scoped atomic operations and assertions. Use the supported transaction grammar and keep batches within the serving contract's limits. This is not a promise of global uniqueness, cross-database transactions or global serializability.

## Aggregations

No SDK wraps aggregates or geo filters yet. The examples below use the HTTP query route with `curl` and `jq`, and the `api` helper and `$DB` from the [vector search shell setup](/fr/guides/vector-search#set-up-the-shell).

Add `aggregate` to a query to get per-group counts and numeric summaries instead of documents:

```bash
api -H 'Content-Type: application/json' --data-binary @- "$DB/collections/orders/query" <<'JSON' | jq
{
  "filter": {"gte": ["/created", 1761264000000]},
  "aggregate": {
    "group_by": ["/status"],
    "sum": ["/amount"], "avg": ["/amount"], "min": ["/amount"], "max": ["/amount"]
  },
  "limit": 100
}
JSON
```

```json
{
  "items": [],
  "next_cursor": null,
  "groups": [
    {"key": ["paid"], "count": "250", "sum": {"/amount": 12500.5}, "avg": {"/amount": 50.002},
     "min": {"/amount": 1}, "max": {"/amount": 99}}
  ]
}
```

- `group_by` takes 0 to 4 paths (JSON Pointers or `$id`). Without it, every match falls in exactly one group.
- A group's `key` has one JSON value per `group_by` path. A missing member groups as `null`. Numbers group by value, so `1` and `1.0` are one group. Arrays and objects group by their JSON text.
- Groups come in key order: `null`, `false`, `true`, numbers, strings, then arrays and objects.
- `sum`, `avg`, `min` and `max` each take distinct JSON Pointers, at most 16 paths across all four. They consider numbers only. When a group has no numbers at a path, `sum` is `0` and the others are `null`. Sums and averages are 64-bit floats.
- `count` is the number of matching documents in the group, as a decimal string like the query `count`.
- `limit` (default 50, at most 1000) caps the number of groups. There is no cursor: more groups than `limit` answers `400 invalid_query` ("the aggregate has more than N groups"). Narrow the filter or raise `limit`.
- `aggregate` cannot be combined with `sort`, `project`, `cursor` or `count`. It works with every filter, including `search`, `within` and end-user collection rules.

A filter on an indexed field seeks that [index](/fr/guides/indexes); the grouping then sorts the group keys of the matches.

## Geospatial queries

A point is an object with numeric `lat` and `lon` members in WGS 84 degrees:

```json
{"name": "Café Central", "loc": {"lat": -23.55, "lon": -46.63}}
```

### Within a radius or a box

A `within` filter matches points at most `meters` (0 to 20000000) from a center, by haversine distance on the WGS 84 mean radius (6371008.8 m):

```json
{"filter": {"within": ["/loc", {"lat": -23.55, "lon": -46.63, "meters": 1000}]}}
```

Or points inside a bounding box. A box with `west` greater than `east` crosses the antimeridian:

```json
{"filter": {"within": ["/loc", {"south": -20, "west": 179, "north": -10, "east": -170}]}}
```

`within` is an ordinary filter node. It nests under `and`, `or` and `not`, and works in counts, aggregates and [transaction](/fr/guides/transactions) assertions. A document without a numeric point is not within any shape. [Schema](/fr/guides/schema-validation) shape filters do not accept `within`.

### Nearest first

A `near` sort with a point orders matches by distance in meters, nearest first:

```bash
api -H 'Content-Type: application/json' --data-binary @- "$DB/collections/places/query" <<'JSON' | jq
{
  "filter": {"within": ["/loc", {"lat": -23.55, "lon": -46.63, "meters": 5000}]},
  "sort": [{"near": {"path": "/loc", "lat": -23.55, "lon": -46.63}}],
  "limit": 10
}
JSON
```

```json
{
  "items": [
    {"id": "01JB…", "version": "…", "state": "value", "document": {"name": "Café Central", "loc": {"lat": -23.5502, "lon": -46.6301}}, "distance": 24.4}
  ]
}
```

Like the [vector `near` sort](/fr/guides/vector-search), it must be the only sort key and returns one page of at most 100 documents with no cursor. Documents without a point are skipped.

### Index tip

A radius filter runs as its bounding box (plain `lat` and `lon` ranges) plus the exact distance test. A btree [index](/fr/guides/indexes) on `loc.lat` (or `loc.lat, loc.lon`) lets the planner seek the latitude band instead of scanning the collection:

```bash
api -H 'Content-Type: application/json' \
  -d '{"collection":"places","fields":["loc.lat","loc.lon"]}' "$DB/indexes" | jq
```

A circle that reaches a pole or crosses the antimeridian keeps every longitude.

GeoJSON geometries, polygons and a dedicated spatial index (R-tree or geohash) are not supported.

## Query limits and errors

Aggregates and geo queries run under the same query work budget and 5-second deadline as every query. A query over budget answers `400 invalid_query` (query too expensive): add a narrower filter or an index. Malformed `aggregate`, `within` or `near` input also answers `400 invalid_query`. See [limits](/fr/limits) and [errors](/fr/guides/errors).

</div>
