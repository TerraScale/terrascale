# Full-text search

Full-text search is available on the live TerraBase API. These helpers require an SDK version containing this change. SDK tests use serialized requests and fake HTTP responses; live backend qualification is recorded separately.

Create a text index using one to four dotted field names:

```rust
use terrascale::v1::{Filter, Index, IndexKind, IndexRequest, Query, SortKey};

let index: Index = db.create_index(&IndexRequest {
    collection: "articles".into(),
    fields: vec!["title".into(), "article.body".into()],
    kind: Some(IndexKind::Text),
    ..Default::default()
}).await?;
```

Query a field using an RFC 6901 pointer, then sort by relevance:

```rust
let query = Query::new()
    .filter(Filter::and([
        Filter::search("/title", "quick fox"),
        Filter::eq("/published", true),
    ]))
    .sort(SortKey::score())
    .limit(20);
let page = db.collection("articles").query::<serde_json::Value>(&query).await?;
// page.items[i].score is Option<f64>; None means the response omitted the score.
```

Terms use native Tantivy syntax; the SDK does not promise advanced query features.
Terms must be nonblank, contain at most 4096 UTF-8 bytes and no NUL, and nest
parentheses at most 64 levels. Malformed native Tantivy syntax returns HTTP 400
with code `invalid_query`. Search paths are pointers, not
dotted index fields; `$id` and `$score` cannot be searched. Missing text indexes
return HTTP 400 with error code `invalid_query`.

`$score` must be descending and the only sort key, with exactly one search
predicate in the filter tree. Ordinary predicates can be combined with that
search. Search cannot combine with `Near`. Higher BM25 scores come first;
document ID ascending breaks ties. Continue with `query.cursor(page.next_cursor.unwrap())`
when `next_cursor` is present, retaining the same filter and sort.

Index creation defaults to B-tree when `kind` is omitted; `IndexKind::BTree` sets
it explicitly. `Index.kind` is available when decoding create or list responses.
Existing generic JSON index methods and `Filter::from_json` remain supported.
