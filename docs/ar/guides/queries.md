---
title: Queries and pagination
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

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

Here `posts` is a collection handle from the [JavaScript quickstart](/ar/getting-started/javascript), and `signal` is an application deadline's `AbortSignal`.

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

The current contract allows at most **4 sort keys**, **64 projection paths**, and a page `limit` from **1 to 1000** (default **50**). A page can stop at its byte or scan budget before reaching that count. A sort may require an index and fail with `sort_requires_index`; do not assume automatic index creation. See [limits](/ar/limits).

C# uses `Filter`, `Sort`, `Project`, `Limit`, `Cursor` and `Count` on `TerraBaseQuery`. Filters can be created with `JsonSerializer.SerializeToElement(new { eq = new[] { "/status", "active" } })`.

## Conditional writes and transactions

Read the current version before a dependent update. JavaScript uses `ifMatch: current.version`; C# uses `TerraBasePrecondition.Matches(current.Version)`. A `412 precondition_failed` means the observed version is no longer current. Refetch and resolve the change before writing.

JavaScript's `database.transaction()` and C#'s `TransactAsync` expose database-scoped atomic operations and assertions. Use the supported transaction grammar and keep batches within the serving contract's limits. This is not a promise of global uniqueness, cross-database transactions or global serializability.

</div>
