---
title: Full-text search
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

# Full-text search

Create a text index over string fields, then use a `search` filter to find documents and sort them by relevance.

::: warning Availability
Full-text search is in development and is not available on the live API yet. These examples describe the API contract being implemented.
:::

## Set up the shell

Use an existing project and database. Replace the example project ULID with your project's ID.

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='01K87X8P6A2Q3B4C5D6E7F8G9H'
export TERRASCALE_DATABASE_NAME='app'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

Queries need `documents:read`, and document writes need `documents:write`. Listing indexes needs `databases:read`; creating an index needs `databases:write`. See [authentication](/guides/authentication).

## Create documents and a text index

```bash
api -H 'Content-Type: application/json' -d '{"name":"articles"}' "$DB/collections"
api -H 'Content-Type: application/json' \
  -d '{"title":"Backup guide","details":{"summary":"Restore your documents from a backup."},"status":"published"}' \
  "$DB/collections/articles/documents" | jq
api -H 'Content-Type: application/json' \
  -d '{"collection":"articles","kind":"text","fields":["title","details.summary"],"name":"articles_text"}' \
  "$DB/indexes" | jq
```

Set `kind` to `"text"` and supply **1 to 4 distinct dotted member paths** in `fields`. The indexed values are strings. For example, `details.summary` names the nested `summary` field inside `details`.

Index definitions use dotted paths; query filters use [JSON Pointer paths](/guides/queries), so `details.summary` becomes `/details/summary` in a query.

List the collection's indexes to check the definition, including its `kind`:

```bash
api "$DB/indexes?collection=articles" | jq '.items'
```

See [indexes](/guides/indexes) for index naming and deletion.

## Search and rank results

Send `{"search":["/title","backup"]}` as the query's `filter`. The first entry identifies an indexed string field; the second is the search text.

Search text must be nonblank, contain no NUL character, fit within **4096 UTF-8 bytes**, and nest parentheses at most **64 levels**. Terms use native Tantivy syntax; malformed syntax returns **`400 invalid_query`**. The examples use plain words; this guide promises no additional advanced query features.

```bash
api -H 'Content-Type: application/json' --data-binary @- "$DB/collections/articles/query" <<'JSON' | jq
{
  "filter": {"search": ["/title", "backup"]},
  "sort": [{"path": "$score", "order": "desc"}],
  "project": ["/title"],
  "limit": 10
}
JSON
```

Use the special sort path `$score` with `order: "desc"` to return higher scores first. Ties are ordered by document ID ascending. `$score` is valid only as a sort path; it cannot be used in `filter` or `project`.

A relevance-sorted query must contain **exactly one `search` predicate**, and `$score` descending must be its **only sort key**. Search cannot be combined with a `near` sort.

When a query sorts by `$score`, its returned items include a numeric `score` alongside their `id` and `document`. A search without `$score` sorting does not request this optional metadata. This illustrative response excerpt shows those fields; the score is an example, not a fixed range or threshold:

```json
{
  "items": [
    {
      "id": "01K87X8P6A2Q3B4C5D6E7F8G9J",
      "document": {"title": "Backup guide"},
      "score": 1.42
    }
  ]
}
```

Read `item.score`, rather than `item.document.score`, when displaying relevance.

Relevance-sorted queries support cursor pagination. Pass the returned continuation cursor unchanged as `cursor`, keeping the same filter, sort and projection. See [queries and pagination](/guides/queries).

## Combine search with other filters

Compose a single `search` with ordinary predicates using `and`, `or` or `not`. For example, return only published articles whose summary matches the search:

```bash
api -H 'Content-Type: application/json' --data-binary @- "$DB/collections/articles/query" <<'JSON' | jq
{
  "filter": {
    "and": [
      {"search": ["/details/summary", "backup"]},
      {"eq": ["/status", "published"]}
    ]
  },
  "sort": [{"path": "$score", "order": "desc"}],
  "project": ["/title", "/details/summary"],
  "limit": 10
}
JSON
```

The index above covers both `title` and `details.summary`; each `search` expression selects the field it searches.

## SDK examples

These examples target the SDK changes for full-text search, which are also in development. Each uses an authenticated database handle named `db`. They are alternatives to the HTTP index and query requests above. See [SDK reference](/guides/sdks) for connection setup.

### JavaScript / TypeScript

```js
import { v1Filter } from '@terrascale/sdk'

await db.indexes.create({
  collection: 'articles',
  kind: 'text',
  fields: ['title', 'details.summary'],
})

const page = await db.collection('articles').query({
  filter: v1Filter.search('/title', 'backup'),
  sort: [{ path: '$score', order: 'desc' }],
  limit: 10,
}, { signal })
const scores = page.items.map(item => ({ id: item.id, score: item.score }))
```

Here `signal` is an application deadline's `AbortSignal`.

### C#

```csharp
using TerraScale.DotNet.TerraBase;

await db.CreateIndexAsync(
    "articles", kind: "text", fields: ["title", "details.summary"]);

var page = await db.QueryAsync("articles", new TerraBaseQuery
{
    Filter = TerraBaseFilter.Search("/title", "backup"),
    Sort = [TerraBaseSort.Score],
    Limit = 10,
}, cancellationToken);
var scores = page.Items.Select(item => new { item.Id, item.Score });
```

### Go

Use these operations inside a function that returns an error; `ctx` is your request context.

```go
import (
    "fmt"
    tb "terrascale.tech/sdk/v1"
)

_, err := db.CreateIndex(ctx, tb.IndexRequest{
    Collection: "articles",
    Kind:       tb.IndexText,
    Fields:     []string{"title", "details.summary"},
})
if err != nil {
    return err
}

page, err := db.Collection("articles").Query(ctx, tb.Query{
    Filter: tb.Search("/title", "backup"),
    Sort:   []tb.SortKey{tb.Desc(tb.ScorePath)},
    Limit:  10,
})
if err != nil {
    return err
}
for _, item := range page.Items {
    if item.Score != nil {
        fmt.Println(item.ID, *item.Score)
    }
}
```

### Rust

```rust
use terrascale::v1::{Filter, Index, IndexKind, IndexRequest, Query, SortKey};

let _: Index = db.create_index(&IndexRequest {
    collection: "articles".into(),
    fields: vec!["title".into(), "details.summary".into()],
    kind: Some(IndexKind::Text),
    ..Default::default()
}).await?;

let query = Query::new()
    .filter(Filter::search("/title", "backup"))
    .sort(SortKey::score())
    .limit(10);
let page = db.collection("articles")
    .query::<serde_json::Value>(&query).await?;
let scores: Vec<_> = page.items.iter().map(|item| (&item.id, item.score)).collect();
```

The score properties are optional: JavaScript uses `item.score`, C# uses nullable `item.Score`, Go uses `item.Score` as `*float64`, and Rust uses `item.score` as `Option<f64>`. Request `$score` sorting when you need relevance metadata.

## Missing text indexes

A search on a path without a matching text index returns **`400 invalid_query`**. Create a `kind: "text"` index for that field in the same collection before retrying. A regular secondary index does not replace a text index.

Check the path spelling in both places: an index on `details.summary` corresponds to a search on `/details/summary`. See [errors and safe retries](/guides/errors) for handling failed requests.
