---
title: Vector similarity search
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Vector similarity search

Store an embedding in each document, then ask for the documents nearest to a query vector. This powers semantic search and retrieval-augmented generation (RAG) without a separate vector database.

Without an index the search is exact (brute force): every document that passes your filter is scored. For large collections, create a [vector index](#index-large-collections) to make it approximate and fast. TerraScale does not create embeddings: use any embedding model and store its output as a JSON array of numbers.

## Set up the shell

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

## Store embeddings

```bash
api -H 'Content-Type: application/json' -d '{"name":"chunks"}' "$DB/collections"
api -H 'Content-Type: application/json' \
  -d '{"kind":"doc","text":"Backups run every hour.","embedding":[0.12,-0.03,0.88]}' \
  "$DB/collections/chunks/documents" | jq
```

Real embeddings have hundreds of numbers; three keep the example short. Every embedding in a collection that you search together must have the same length.

## Query the nearest documents

Send a `near` sort to the [query route](/zh-cn/guides/queries):

```bash
api -H 'Content-Type: application/json' --data-binary @- "$DB/collections/chunks/query" <<'JSON' | jq
{
  "filter": {"eq": ["/kind", "doc"]},
  "sort": [{"near": {"path": "/embedding", "vector": [0.10, -0.01, 0.90], "metric": "cosine"}}],
  "project": ["/text"],
  "limit": 5
}
JSON
```

Results come nearest first (ties by id). Each item carries a `distance`:

```json
{
  "items": [
    {"id": "01JB…", "version": "…", "state": "value", "document": {"text": "Backups run every hour."}, "distance": 0.0007}
  ]
}
```

- `metric` is `cosine` (the default, `1 - cosine similarity`) or `l2` (Euclidean distance). Distances are computed in 32-bit floats.
- `near` combines with `filter`, `project` and `count`. Filter first to score fewer documents.
- Only documents whose `path` is an array of numbers of the query vector's length are scored. Other documents are skipped, not errors.

## Index large collections

Brute force scores every document, so it slows down as a collection grows. A vector index makes `near` queries approximate and fast:

```bash
api -H 'Content-Type: application/json' \
  -d '{"collection":"chunks","kind":"vector","fields":["embedding"],"dimensions":768,"name":"chunks_embedding"}' \
  "$DB/indexes" | jq
```

- `fields` holds exactly one dotted member path to the embedding array, and `dimensions` is its length (1 to 4096).
- On a collection that already has documents, the response says `"state": "building"`. Existing documents are added in the background; new writes are indexed as they happen, and a write waits at most for one small batch of the build. Until the index lists as `"active"` (`GET $DB/indexes?collection=chunks`), `near` queries keep scanning.
- A `near` query whose `path` and vector length match an active index uses it, with either metric. Nothing else changes in the request or response.
- Results are approximate. The index shortlists candidates by a compact sign-bit code of each vector, then ranks the shortlist with exact distances. In our benchmark on 100,000 real 768-dimension text embeddings, 99.65% of the exact top 10 came back.
- With a `filter`, the filter applies to the 4,096 closest candidates only. A very selective filter can return fewer than `limit` documents; brute force (no index) never misses.
- Documents whose `path` is not an array of exactly `dimensions` numbers are not in the index and are never returned by indexed queries.
- Reads restricted by [collection access rules](/zh-cn/guides/end-user-authentication) do not use the index.

Delete the index with `DELETE $DB/indexes/chunks_embedding`; `near` queries go back to scanning. See [indexes](/zh-cn/guides/indexes).

## Limits

| Limit | Value |
| --- | --- |
| Vector length | 1 to 4096 numbers, each within the 32-bit float range |
| Cosine vector | Not all zeros |
| Sort keys | `near` is the only sort key: no `path` or `order` beside it |
| Results | One page, `limit` at most 1000, no `next_cursor`; a `cursor` is rejected |
| Page size | 8 MiB of JSON, as for every query |
| Work | The query work budget and 5-second deadline apply |
| Document size | 1 MiB, which bounds how large an embedding can be |

A request outside these limits answers `400 invalid_query`. A brute-force search over too many documents answers `400 invalid_query` (query too expensive): create a [vector index](#index-large-collections), or add a filter, such as a tenant or category, to narrow the candidates. See [limits](/zh-cn/limits).

## RAG example (JavaScript)

Retrieve the five closest chunks, then hand them to your model as context. `embed` and `generate` are your own functions for your embedding and language model.

```js
import { v1Filter } from '@terrascale/sdk'

const chunks = client.database('app').collection('chunks')

async function answer(question, { signal }) {
  const vector = await embed(question)
  const { items } = await chunks.query({
    filter: v1Filter.eq('/kind', 'doc'),
    sort: [{ near: { path: '/embedding', vector, metric: 'cosine' } }],
    project: ['/text'],
    limit: 5,
  }, { signal })
  const context = items.map((item) => item.document.text).join('\n---\n')
  return generate(`Answer from this context only:\n${context}\n\nQuestion: ${question}`)
}
```

Each item has `distance`, nearest first. Drop items above a distance threshold you choose to avoid feeding unrelated text to the model. The SDK checks the [limits](#limits) before sending the request.

## From other SDKs

Go (`terrascale.tech/sdk/v1`):

```go
res, err := chunks.Query(ctx, tb.Query{
	Filter: tb.Eq("/kind", "doc"),
	Sort:   []tb.SortKey{tb.Near("/embedding", vector, tb.Cosine)},
	Limit:  5,
})
for _, d := range res.Items { fmt.Println(d.ID, *d.Distance) }
```

C# (`TerraScale.DotNet`):

```csharp
var page = await database.QueryAsync("chunks", new TerraBaseQuery
{
    Sort = [TerraBaseSort.Near("/embedding", embedding, "cosine")],
    Limit = 5,
}, cancellationToken);
foreach (var item in page.Items) Console.WriteLine($"{item.Id} {item.Distance}");
```

Rust (`terrascale::v1`):

```rust
use terrascale::v1::{Filter, Metric, Near, Query};

let q = Query::new()
    .filter(Filter::eq("/kind", "doc"))
    .near(Near::new("/embedding", embedding).metric(Metric::Cosine))
    .limit(5);
for doc in chunks.query::<serde_json::Value>(&q).await?.items {
    println!("{} {:?}", doc.id, doc.distance);
}
```

</div>
