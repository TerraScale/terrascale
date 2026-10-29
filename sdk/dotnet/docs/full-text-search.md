# Full-text search

Full-text search is available on the live TerraBase API. These helpers require an SDK version containing this change. SDK tests use serialized requests and fake HTTP responses; live backend qualification is recorded separately.

```csharp
using TerraScale.DotNet.TerraBase;

await db.CreateIndexAsync(
    "articles", kind: "text", fields: ["title", "metadata.summary"]);

var query = new TerraBaseQuery
{
    Filter = TerraBaseFilter.Search("/title", "database storage"),
    Sort = [TerraBaseSort.Score],
    Limit = 20,
};
var first = await db.QueryAsync("articles", query);
var scores = first.Items.Select(item => item.Score);
if (first.NextCursor is { } cursor)
{
    var next = await db.QueryAsync("articles", query with { Cursor = cursor });
}
```

Index creation accepts 1–4 distinct dotted field paths. The existing `CreateIndexAsync` overload keeps the producer's btree default. The new overload accepts `kind: "text"` or `kind: "btree"`; the optional `name` still defaults to a generated name. Create/list results expose `Kind` when the producer sends it; older responses without the field remain supported. Existing positional method calls, constructors, and deconstruction remain unchanged.

`TerraBaseFilter.Search(path, terms)` returns a `JsonElement` containing `{"search":["/title","database storage"]}`. Paths are RFC 6901 JSON Pointer strings, including `~0` and `~1` escapes. `$id`, `$score`, dotted strings, and segment arrays are not search paths. Terms must be nonblank, at most 4096 UTF-8 bytes, contain no NUL, and nest parentheses at most 64 levels. Malformed native Tantivy syntax returns HTTP 400 with code `invalid_query`. Terms pass unchanged to the native Tantivy query parser; this SDK promises no additional advanced query features. Raw `JsonElement` filters remain supported, including boolean composition of search with ordinary predicates.

`TerraBaseSort.Score` emits `{"path":"$score","order":"desc"}`. `$score` is a sort-only pseudo-path. Relevance sorting must use `desc` as the only sort key, with exactly one search predicate in the filter tree. Search cannot combine with vector `near` sorting. Results use higher BM25 score first, with document ID ascending for ties. `Score` is nullable relevance metadata requested by `$score` sorting; a search without that sort does not request it. Pagination supports score/ID cursors; retain the same filter, sort, and projection when using `NextCursor`.

A missing text index returns HTTP 400 with code `invalid_query`, preserved as `TerraBaseException.ErrorCode`. The SDK does not retry it.
