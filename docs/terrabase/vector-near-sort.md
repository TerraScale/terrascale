# Vector similarity sort (`near`)

`POST /v1/projects/{project}/databases/{database}/collections/{collection}/query` accepts one sort key of the form

```json
{"filter": {"eq": ["/kind", "doc"]},
 "sort": [{"near": {"path": "/embedding", "vector": [0.1, 0.2, 0.3], "metric": "cosine"}}],
 "limit": 10}
```

- `metric` is `cosine` (default, `1 - cosine similarity`) or `l2` (Euclidean distance). Results come nearest first, ties by id.
- Each item carries `distance`. Values compare as float32. The `tb_near` SQL function (`Engine/Sqlite/NearDistance.cs`)
  walks each document's bytes once to the path; profiling showed `json_extract` of a 128-number array at about 50 µs per
  row, several times the whole walk.
- `vector` has 1 to 4096 numbers within the float32 range; a cosine vector is not all zeros.
- Only documents whose `path` is an array of the same length with only numbers match; others (strings, booleans,
  nested arrays, other lengths) are skipped, not errors.
- `near` is the only sort key. It combines with `filter`, `project` and `count` (count is of filter matches, with or
  without a comparable vector).
- One page only: `limit` is at most 100, there is no `next_cursor`, and a request with `cursor` is rejected.
  The 8 MiB page cap still applies.
- Brute force: every candidate after the filter is scored. The existing query work budget and 5-second deadline apply
  (400 `invalid_query`, query too expensive); the 10000-candidate field-sort budget does not, because the sorter
  holds only id, version, size and distance per candidate.

Not included: an ANN/vector index (the shipped Turso native library has only a sparse toy index) and embedding
generation. Latency: `benchmark/bench/2026-10-23-vector-near.md`.
