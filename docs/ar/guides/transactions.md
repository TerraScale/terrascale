---
title: Transactions
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Transactions

A transaction applies 1 to 1024 operations across the collections of one database. Either every operation commits, or none does.

## Set up the shell

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

Transactions need `documents:write`. A transaction with assertions also needs `documents:read`.

## Run a transaction

This places an order only if the account is active and has fewer than 100 orders:

```bash
for collection in accounts orders items; do api -H 'Content-Type: application/json' -d "{\"name\":\"$collection\"}" "$DB/collections"; done
api -X PUT -H 'Content-Type: application/json' -H 'If-None-Match: *' -d '{"status":"active"}' "$DB/collections/accounts/documents/by-key?id=01JB7Q3V0Z8K4M2N6P9R5T1W6A" | jq
api -X PUT -H 'Content-Type: application/json' -H 'If-None-Match: *' -d '{"stock":3}' "$DB/collections/items/documents/by-key?id=01JB7Q5A1B3C5D7E9F0G2H4J6K" | jq
api -H 'Content-Type: application/json' --data-binary @- "$DB/transactions" <<'JSON' | jq
{"ops": [
  {"op": "assert", "collection": "accounts", "id": "01JB7Q3V0Z8K4M2N6P9R5T1W6A",
   "filter": {"eq": ["/status", "active"]}},
  {"op": "assert_count", "collection": "orders",
   "filter": {"eq": ["/account", "01JB7Q3V0Z8K4M2N6P9R5T1W6A"]}, "lte": 99},
  {"op": "create", "collection": "orders", "id": "01JB7Q4D2E6F8G0H1J3K5M7N9P",
   "document": {"account": "01JB7Q3V0Z8K4M2N6P9R5T1W6A", "total": 4200}},
  {"op": "patch", "collection": "items", "id": "01JB7Q5A1B3C5D7E9F0G2H4J6K",
   "ops": [{"op": "inc", "path": "/stock", "by": -1}]}
]}
JSON
```

A `200` returns `offset`, `durability` and one result per operation, in order. Writes return `id` and `version`; a patch also returns the stored `document`. Assertions return `asserted: true`, and `assert_count` adds the decimal `count`.

## Operations

| `op` | Members |
| --- | --- |
| `create` | `document`; optional `id`. Fails if the document exists. Without `id`, the server generates a ULID. |
| `put` | `id`, `document`; optional `if_match` (a version) or `if_none_match: "*"` |
| `patch` | `id` and exactly one of `merge` or `ops`; optional `if_match` |
| `delete` | `id`; optional `if_match` |
| `assert` | `id` and either `filter` (the document exists and matches) or `absent: true` |
| `assert_count` | `filter` (optional), `gte` and/or `lte`; no `id` |

Filters use the same JSON filter grammar as [queries](/ar/guides/queries). Patch `ops` support `set`, `unset`, `append`, `inc`, `add` and `remove`. Document ids are ULIDs.

Rules:

- Two operations may not target the same document.
- Assertions see the state before this transaction's writes.
- At most 16 `assert_count` operations per transaction. Each examines at most 10000 documents; more answers `400 assert_requires_index`. Narrow the filter or add an [index](/ar/guides/indexes).
- Captured changes fit 2 MiB, including encoded document state, materialized JSON, collection names and ids. This is separate from the 4 MiB request-body and 1 MiB document limits; JSON smaller than 2 MiB can still exceed the capture budget. Split larger writes into several transactions.

## With the JavaScript SDK

`@terrascale/sdk` 0.0.3 wraps this route as `database.transaction()`:

```js
import { v1Filter } from '@terrascale/sdk'

const result = await database.transaction()
  .assert('accounts', accountId, v1Filter.eq('/status', 'active'))
  .assertCount('orders', v1Filter.eq('/account', accountId), { lte: 99 })
  .create('orders', { account: accountId, total: 4200 })
  .commit({ signal })
```

`database` comes from `client.database(name)`. A failure throws `TerraBaseV1Error`; its `op` names the failing operation.

## Failures

A deterministic operation rejection rolls back the whole transaction. `error.op` is the zero-based index of the failing operation. An uncertain write response does not prove rollback; reconcile the affected documents before resubmitting.

| Response | Cause |
| --- | --- |
| `400 invalid_request` | Malformed body, more than 1024 operations, more than 16 `assert_count`, or two operations on one document |
| `400 invalid_patch` / `invalid_query` / `schema_validation_failed` | Invalid patch, a filter too expensive to run, or a document that fails the [collection schema](/ar/guides/schema-validation) |
| `403 forbidden_scope` | Missing scope, such as `documents:read` for assertions |
| `404 unknown_collection` / `not_found` | Missing collection or document |
| `409 patch_conflict` / `unique_violation` | Patch does not fit the stored value, or a [unique key](/ar/guides/schema-validation#declare-unique-keys) is taken |
| `412 precondition_failed` | `if_match` or `if_none_match` failed, `create` found an existing document, or an assertion did not hold |
| `503 capacity` | Captured changes exceed the 2 MiB budget; split the transaction after a deterministic rejection |

A capture-budget overflow discovered while finalizing changes can currently return `503 write_outcome_unknown` instead of `capacity`. Reconcile the affected documents before splitting or resubmitting; do not infer rollback from this error.

A `503` with `Retry-After`, such as `remote_region_unavailable` or `confirmation_busy`, was rejected before execution and can be retried within a deadline. A `503 write_outcome_unknown` means the commit may or may not have happened. Never replay it automatically: read the affected documents first. See [errors](/ar/guides/errors).

## Durability

`durability` defaults to `local_durable` after the serving region persists the write, even when other regions hold the database; replication continues in the background. Add `-H 'TB-Durability: remote_durable'` to the transaction request to wait up to 10 seconds for another placed region to confirm persistence. A database placed only in its home region refuses writes with `409 placement_needs_two_regions`. Assertions alone return `unchanged`.

</div>
