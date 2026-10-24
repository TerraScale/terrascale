# Full-text search

Full-text search is available on the live TerraBase API. These helpers require an SDK version containing this change. SDK tests use serialized requests and fake HTTP responses; live backend qualification is recorded separately.

```js
import { v1Filter } from "@terrascale/sdk";

await db.indexes.create({
  collection: "articles",
  fields: ["title", "metadata.summary"],
  kind: "text",
});

const articles = db.collection("articles");
const query = {
  filter: v1Filter.and(
    v1Filter.eq("/published", true),
    v1Filter.search("/title", "database storage"),
  ),
  sort: [{ path: "$score", order: "desc" }],
  limit: 20,
};
const first = await articles.query(query);
const scores = first.items.map(item => item.score);
if (first.nextCursor !== null) {
  const next = await articles.query({ ...query, cursor: first.nextCursor });
}
```

Index creation accepts 1–4 distinct dotted field paths. Omitting `kind` keeps the existing btree default; `btree` can also be explicit. Create/list results expose `kind` when the producer sends it; older responses without the field remain supported.

`v1Filter.search(path, terms)` emits `{"search":["/title","database storage"]}`. Paths are RFC 6901 JSON Pointer strings, including `~0` and `~1` escapes. `$id`, `$score`, dotted strings, and segment arrays are not search paths. Terms must be nonblank, at most 4096 UTF-8 bytes, contain no NUL, and nest parentheses at most 64 levels. Malformed native Tantivy syntax returns HTTP 400 with code `invalid_query`. Terms pass unchanged to the native Tantivy query parser; this SDK promises no additional advanced query features. Raw filter objects remain supported.

`$score` is a sort-only pseudo-path. Relevance sorting must use `desc` as the only sort key, with exactly one search predicate in the filter tree. Boolean composition with ordinary predicates is supported. Search cannot combine with vector `near` sorting. Results use higher BM25 score first, with document ID ascending for ties. `item.score` is optional relevance metadata requested by `$score` sorting; a search without that sort does not request it. Pagination supports score/ID cursors; retain the same filter, sort, and projection when using `nextCursor`.

A missing text index returns HTTP 400 with code `invalid_query`. The SDK preserves the error and does not retry it.
