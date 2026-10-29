# Full-text search

Full-text search is available on the live TerraBase API. These helpers require an SDK version containing this change. SDK tests use serialized requests and fake HTTP responses; live backend qualification is recorded separately.

Create a text index using one to four dotted field names:

```go
index, err := db.CreateIndex(ctx, v1.IndexRequest{
    Collection: "articles", Fields: []string{"title", "article.body"},
    Kind: v1.IndexText,
})
```

Query a field using an RFC 6901 pointer, then sort by relevance:

```go
page, err := db.Collection("articles").Query(ctx, v1.Query{
    Filter: v1.And(v1.Search("/title", "quick fox"), v1.Eq("/published", true)),
    Sort: []v1.SortKey{v1.Desc(v1.ScorePath)},
    Limit: 20,
})
// page.Items[i].Score is *float64; nil means the response omitted the score.
```

Terms use native Tantivy syntax; the SDK does not promise advanced query features.
Terms must be nonblank, contain at most 4096 UTF-8 bytes and no NUL, and nest
parentheses at most 64 levels. Malformed native Tantivy syntax returns HTTP 400
with code `invalid_query`. Search paths are pointers, not
dotted index fields; `$id` and `$score` cannot be searched. Missing text indexes
return HTTP 400 with `invalid_query` (`v1.ErrInvalidQuery`).

`$score` must be descending and the only sort key, with exactly one search
predicate in the filter tree. Ordinary predicates can be combined with that
search. Search cannot combine with `Near`. Higher BM25 scores come first;
document ID ascending breaks ties. Pass `page.NextCursor` as `Query.Cursor` to
continue using the same filter and sort.

Index creation defaults to B-tree when `Kind` is omitted; `v1.IndexBTree` sets it
explicitly. Create and list responses expose `Index.Kind`. Existing APIs are unchanged.
