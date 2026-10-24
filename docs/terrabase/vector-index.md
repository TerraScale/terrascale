# Vector indexes (`kind: vector`)

A vector index makes a `near` sort (see `vector-near-sort.md`) approximate and fast on large collections.

```http
POST /v1/projects/{project}/databases/{database}/indexes
{"collection": "articles", "kind": "vector", "fields": ["embedding"], "dimensions": 768}
```

- One field, a dotted member path, and `dimensions` 1 to 4096. Other kinds reject `dimensions`. The index is listed
  with `kind: vector` and its `dimensions`, replicates like other index definitions, and is dropped with
  `DELETE …/indexes/{name}` or with its collection.
- A `near` query whose path and vector length match an index uses it, with either metric (`cosine` or `l2`); any other
  `near` query, or an owner-scoped read, still scans the collection.
- `near` now returns up to `limit` 1000 documents (was 100), indexed or not. Still one page, no cursor.

## How it works

Turso 0.8.2, the vendored engine, has no dense vector index: its only vector index method,
`toy_vector_sparse_ivf`, serves Jaccard distance over sparse vectors, and there is no DiskANN or `libsql_vector_idx`.
TerraBase therefore keeps a derived index in the database file, next to the collection:

- `_tb_vector_indexes` holds each definition and its build position; `_tb_vec_<name>` maps a slot number to a document
  id and its vector as little-endian float32 values; `_tb_vec_<name>__codes` packs 128 slots per row: a 16-byte
  occupancy bitmap, then each slot's code, one sign bit per dimension (value > 0), padded to 8 bytes. 100000 vectors of
  768 dimensions are 782 code rows of about 12 KiB and about 300 MiB of float32 values.
- Every document write updates its slot in the same transaction as the document row: a document without a numeric
  array of exactly `dimensions` finite float32 values at the path leaves the index.
- Built online: creating an index on a nonempty collection answers `state: building` at once. Writes from then on are
  indexed with themselves; `VectorIndexBuilder` adds the existing documents 256 per write transaction, in id order,
  remembering its position (`_tb_vector_indexes.built`), so writers wait for one batch at most. Until the index lists
  as `active`, `near` queries scan as without an index. A node resumes unfinished builds at start and every hour, which
  also covers an index that reached a node by replication.
- A query reads every code row, keeps the `max(400, 4 × limit)` slots with the smallest Hamming distance to the query's
  sign bits (4096 when the query has a filter), computes their exact float32 distances from the stored values, and hands
  the nearest `limit` ids (every candidate, nearest first, when there is a filter) to the ordinary `near` statement,
  which reads only those documents, applies the filter and returns them with `tb_near` distances. Results are exact
  among the candidates; a document outside them is missed. Reranking from stored float32 values instead of document
  JSON was the main cost in profiling: parsing 400 arrays of 768 JSON numbers took most of a query.
- With a filter, only those 4096 candidates are filtered, so a very selective filter can return fewer than `limit`
  documents even when more matches exist.

On real embeddings sign bits keep neighbourhoods well: on 100000 OpenAI `text-embedding-3-large` vectors truncated to
768 dimensions, an offline simulation reranking the 400 closest codes found 99.7% of the exact top 10. Random
(isotropic) vectors are the worst case for any approximate index and recall less.

## Measurements

`benchmark/cmd/ann-bench` loads the corpus, then three times rebuilds the index and runs held-out queries, comparing
each answer with exact neighbours. Reports: `benchmark/bench/*-ann-*.md`.
