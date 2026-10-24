# Query aggregates and geo filters

Both extend `POST /v1/projects/{project}/databases/{database}/collections/{collection}/query` (query v1) and run
inside Turso under the existing query work budget and 5-second deadline (400 `invalid_query`, query too expensive).

## Aggregates

```json
{"filter": {"gte": ["/created", 1761264000000]},
 "aggregate": {"group_by": ["/status"], "sum": ["/amount"], "avg": ["/amount"], "min": ["/amount"], "max": ["/amount"]},
 "limit": 100}
```

```json
{"items": [], "next_cursor": null,
 "groups": [{"key": ["paid"], "count": "250", "sum": {"/amount": 12500.5}, "avg": {"/amount": 50.002},
             "min": {"/amount": 1}, "max": {"/amount": 99}}]}
```

- `group_by` has 0 to 4 paths (JSON Pointers or `$id`). Without it there is exactly one group of every match.
- A key has one JSON value per `group_by` path. Missing members group with `null`; numbers group by value
  (`1` and `1.0` are one group); arrays and objects group by their JSON text. Groups come in sort order
  (`null < false < true < numbers < strings < arrays/objects`).
- `sum`, `avg`, `min` and `max` each take distinct JSON Pointers, at most 16 paths in total, and consider numbers
  only. Without numbers `sum` is `0` and the others are `null`. Sums and averages are 64-bit floats.
- `count` is the group's matching documents, as a decimal string like the query `count`.
- `limit` (default 50, at most 1000) bounds the groups: more groups answer 400 `invalid_query`
  ("the aggregate has more than N groups"). There is no cursor.
- `aggregate` takes no `sort`, `project`, `cursor` or `count`. It combines with every filter, including `search`,
  `within` and end-user collection rules. A filter on an indexed field seeks that index; grouping itself sorts the
  group keys of the matches.

## Geo filters and distance order

Points are objects with numeric `lat` and `lon` members (WGS 84 degrees), for example `{"loc": {"lat": -23.55, "lon": -46.63}}`.

- `{"within": ["/loc", {"lat": -23.55, "lon": -46.63, "meters": 1000}]}` matches points at most `meters`
  (0 to 20000000) away, by haversine distance on the WGS 84 mean radius (6371008.8 m).
- `{"within": ["/loc", {"south": -20, "west": 179, "north": -10, "east": -170}]}` matches points in a box.
  `west > east` crosses the antimeridian.
- `within` is an ordinary filter node: it nests under `and`, `or` and `not` (a document without a numeric point
  is simply not within), and works in counts, aggregates and transaction assertions. Shape filters reject it.
- `sort: [{"near": {"path": "/loc", "lat": -23.55, "lon": -46.63}}]` orders by meters from that point, nearest first,
  returned as each item's `distance`. Like the vector `near`, it is the only sort key and returns one page of at most
  100 documents without a cursor; documents without a point are skipped.

Index: a circle becomes its bounding box (plain `lat` and `lon` ranges) plus the exact distance test, so a btree
index on `loc.lat` (optionally `loc.lat, loc.lon`) lets the planner seek the latitude band instead of scanning
the collection. A circle that reaches a pole or crosses the antimeridian keeps every longitude.

Not included: GeoJSON geometries, polygons, and a dedicated spatial (R-tree or geohash) index.
