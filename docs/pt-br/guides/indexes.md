---
title: Indexes
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Indexes

A secondary index orders a collection by one to four fields, so filters and sorts on those fields do not scan the whole collection. A sort that needs an index fails with `sort_requires_index`. See [queries](/pt-br/guides/queries).

No SDK wraps index management yet. The examples use the public HTTP routes with `curl` and `jq`.

## Set up the shell

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

Reads need `databases:read`. Creating, dropping and applying need `databases:write`. See [authentication](/pt-br/guides/authentication).

## Create an index

```bash
api -H 'Content-Type: application/json' \
  -d '{"collection":"posts","fields":["status","createdAt"],"name":"posts_status_created"}' \
  "$DB/indexes" | jq
```

- `collection`: the collection to index.
- `fields`: 1 to 4 distinct dotted member paths, such as `profile.name`. `_id` and `_rev` are not allowed; the primary key already orders them.
- `name` (optional): a lowercase letter, then lowercase letters, digits or `_`, at most 63 characters. When omitted, the name is derived from the collection and fields. Names are unique per database.

The index is built before the response. A `201` returns the index with `state` set to `active`. An existing index answers `409 index_exists`.

`kind` (optional) is `btree` (the default), `text` for [full-text search](/pt-br/guides/full-text-search), or `vector` for fast [`near` queries](/pt-br/guides/vector-search#index-large-collections) over one embedding field with its `dimensions`. A vector index over existing documents answers `state: building` and becomes `active` once a background build has added them.

## List and drop indexes

```bash
api "$DB/indexes?collection=posts" | jq '.items'
api -X DELETE "$DB/indexes/posts_status_created"
```

The list is in name order. Omit `collection` to list every index in the database. A drop answers `204`.

## Index recommendations

TerraBase records fields that SQL queries filtered or sorted on while no index of the collection led with that field. It recommends one single-field index per collection and field, most queried first.

```bash
api "$DB/index-recommendations" | jq '.items[] | {id, reason, queries}'
api -X POST "$DB/index-recommendations/posts:status/apply" | jq
```

A recommendation `id` is `collection:field`. Applying it answers `201` with `status: "applied"` and the new index under its default name. If an index already leads with that field, it answers `200` with `status: "already-applied"` and that index. A `409 name_taken` means another index already uses the default name.

## Automatic index policy

Automatic index maintenance is not available through these routes. They store a policy only and do not create or remove indexes. Create and drop indexes explicitly with the manual operations above.

```bash
api "$DB/auto-index/posts" | jq
api -X PUT -H 'Content-Type: application/json' -d '{"enabled":false}' "$DB/auto-index/posts" | jq
```

`enabled` is required. `false` records an opt-out; `true` restores the default policy. Neither value starts maintenance. The collection does not need to exist yet. Manual index operations work either way. At most 1000 collections per database can record an opt-out; one more answers `400 auto_index_policy_limit`.

## Query shapes

```bash
api "$DB/query-shapes" | jq '.items[] | {collection, shape, queries, maxLatencyMs, rowsScanned}'
```

This lists up to 128 observed query shapes per database, with literal values removed, most queried first. Each shape reports its query count, total and maximum latency in milliseconds, rows scanned and `lastSeenAt`. Statistics are best-effort: new shapes are dropped when the list is full, and recent observations can appear with a delay. Use high `rowsScanned` counts to decide which fields to index.

## Errors

| Response | Meaning |
| --- | --- |
| `400 invalid_request` | Invalid collection, fields or name |
| `403 forbidden_scope` | The token lacks the required scope |
| `404 not_found` | Unknown index or recommendation |
| `409 index_exists` / `409 name_taken` | The index or its name already exists |
| `503` | Retry within a deadline, honoring `Retry-After` where supplied |

See [errors](/pt-br/guides/errors).

</div>
