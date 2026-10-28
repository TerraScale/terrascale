---
title: Schema validation and unique keys
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Schema validation and unique keys

Collections start schemaless. Two separate routes add rules to a collection:

| Route | Purpose | Scope |
| --- | --- | --- |
| `…/collections/{collection}/schema` | A JSON Schema that every written document must satisfy | `documents:write` (`documents:read` to read) |
| `…/schema/{collection}` | Unique keys, plus the fields inferred from stored documents | `databases:write` (`databases:read` to read) |

The HTTP examples below use `curl` and `jq`. For TypeScript tooling, see [Generate TypeScript document models](#generate-typescript-document-models).

## Set up the shell

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
DB="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

Both routes need an existing collection. Create one with `api -H 'Content-Type: application/json' -d '{"name":"orders"}' "$DB/collections"`.

## Attach a JSON Schema

The request body is the schema itself:

```bash
api -X PUT -H 'Content-Type: application/json' --data-binary @- "$DB/collections/orders/schema" <<'JSON' | jq
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["quantity"],
  "properties": {"quantity": {"type": "integer", "minimum": 0}}
}
JSON
```

The answer is `200 {"schema": …, "dialect": "https://json-schema.org/draft/2020-12/schema"}`. A second PUT replaces the schema.

- The dialect is JSON Schema Draft 2020-12, also when `$schema` is absent.
- A schema is at most 64 KiB, 15 nesting levels, 4093 JSON values and 128 subschemas, references included.
- References must be local, such as `#/$defs/item`. External references, `$id`, anchors, dynamic references, `pattern`, `patternProperties`, `definitions` and `dependencies` are rejected. Use `$defs`, `dependentSchemas` or `dependentRequired`.
- `format` is an annotation only.

An invalid schema answers `400 invalid_schema` and keeps the previous schema. An oversized body answers `413 payload_too_large`.

## Read or remove the schema

```bash
api "$DB/collections/orders/schema" | jq
api -X DELETE "$DB/collections/orders/schema" | jq
```

Without a schema, the answer is `{"schema": null}`. Removing a schema keeps every document.

## Generate TypeScript document models

`@terrascale/sdk@0.0.17` includes Node-only `@terrascale/sdk/codegen` and `terrascale-models` tooling. Install its pinned tooling peer with `npm install --save-dev @terrascale/sdk@0.0.17 json-schema-to-typescript@16.0.0 typescript`. Keep codegen imports outside browser bundles; ordinary SDK applications do not need the generator peer.

Download the complete [typed-model fixture](/examples/typed-models/README.txt), pinned to published SDK 0.0.17. It generates models deterministically and strictly compiles the required `quantity`, nullable `note`, and invalid create/patch cases.

For a local declaration, run `npx terrascale-models schema.json Order models.ts`. The input is the JSON Schema itself, without the API response wrapper. The equivalent API is `await generateDocumentModels(schema, 'Order')`. It returns a deterministic TypeScript string; regenerate after every schema change.

To fetch an owned collection's declaration, save this as `generate-models.mjs` and run `node generate-models.mjs`. The collection needs an attached schema: if you removed it in the previous section, attach it again first, or the generator throws `collection has no declared JSON Schema`. Use your existing authorized endpoint, project, database and token configuration; never pass secrets as CLI arguments. Automatic region discovery has not shipped, so the SDK currently requires `baseUrl` or an endpoint resolver.

```js
import { writeFile } from 'node:fs/promises'
import { createTerraBaseClient } from '@terrascale/sdk'
import { generateCollectionModels } from '@terrascale/sdk/codegen'
const client = createTerraBaseClient({
  baseUrl: process.env.TERRASCALE_API_URL,
  project: process.env.TERRASCALE_PROJECT_ID,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
})
const orders = client.database(process.env.TERRASCALE_DATABASE_NAME).collection('orders')
await writeFile('models.ts', await generateCollectionModels(orders, 'Order'))
```

`generateCollectionModels(collection, 'Order', { signal })` accepts an optional abort signal and calls `collection.getSchema()` with `documents:read`. It fails without a declaration, never reads samples, and never attaches a schema.

Output contains `OrderDocument`, `OrderCreate` and `OrderPatch` body aliases, separate from server envelope fields `id` and `version`. Use a generated create value with `orders.create(body)` or `orders.put(id, body)`, and a patch with `orders.patch(id, { merge: patch })`. For `{"type":"object","required":["quantity"],"additionalProperties":false,"properties":{"quantity":{"type":"integer"},"note":{"type":["string","null"]}}}`, `const body: OrderCreate = { quantity: 2 }` compiles; `const invalid: OrderCreate = {}` fails because `quantity` is required. Enable `strict` and `exactOptionalPropertyTypes` in `tsconfig.json`.

Merge-patch object fields are recursively optional. `null` deletes optional fields; required fields cannot be deleted, even if nullable. `{ note: null }` is a valid `OrderPatch` that removes the note. To store explicit null, use `orders.patch(id, { ops: [{ op: 'set', path: '/note', value: null }] })`. Arrays replace whole arrays and require complete item shapes.

The root must declare `type: object`. The subset supports explicit scalar types, nullable unions of one type plus null, objects with `properties`/`required`, open objects, closed objects, pure typed maps, homogeneous arrays with `items`, and scalar `enum`/`const` (container literal constraints are rejected). Typed additional properties mixed with named properties are rejected because TypeScript cannot represent that restriction exactly. Numeric bodies retain `number | ExactDecimal`; write inputs also accept `bigint`. Unrepresentable exact numeric literals produce diagnostics.

A property's `readOnly: true` preserves its document field and forbids create/patch inputs. The server neither generates that field nor enforces the annotation. Required read-only fields are diagnosed because the server cannot populate them; `default` never generates a value or makes a required field optional. Generated document IDs remain envelope fields; no generated-field schema extension is assumed.

References, compositions, conditionals, tuples, patterns, unknown keywords and invalid supported keyword values produce JSON Pointer diagnostics. Inline references or simplify to this subset. Runtime validation remains authoritative for ranges, lengths and other constraints; `format` is annotation-only. The server supports a broader schema language. SDK reads remain unvalidated JSON, and historical documents may predate the declaration; generated aliases do not validate or narrow reads automatically.

## What happens on an invalid write

Creates, puts, both patch formats and [transactions](/ru/guides/transactions) validate the complete resulting document. A failing write answers `400 schema_validation_failed` and changes nothing:

```json
{
  "error": {
    "code": "schema_validation_failed",
    "message": "the document does not satisfy the collection schema",
    "details": [{"path": "/quantity", "keyword": "minimum"}]
  }
}
```

`path` is a JSON Pointer; the empty string is the document root. `details` holds at most 16 failures and never includes document values. In a transaction, `error.op` names the failing operation and the whole transaction rolls back. Deletes and assertions are not validated.

Validation applies to future writes only. Attaching a schema does not check or rewrite existing documents.

## Declare unique keys

Unique keys use the other route, `PUT …/schema/{collection}`, and need an empty collection:

```bash
api -X PUT -H 'Content-Type: application/json' \
  -d '{"fields":[{"name":"email","unique":true,"normalization":"lowercase"}],"indexes":[{"name":"tenant_handle","fields":["tenant","handle"],"unique":true}]}' \
  "$DB/schema/orders" | jq '.constraints'
```

- `fields[]`: a single unique field. Only entries with `unique: true` add a key.
- `indexes[]`: a named compound key of 1 to 4 dotted paths.
- `normalization`: `none` (default), `lowercase` or `trim_lowercase`, applied to strings.
- At most 32 keys. Each key fits 16 KiB, and all keys of one document fit 64 KiB.

Missing or `null` values do not claim a key. Arrays and objects are invalid unique values. Repeating an unchanged declaration is safe. Changing keys on a collection with documents answers `409 schema_requires_empty_collection`.

A duplicate write answers `409 unique_violation` and rolls back its whole transaction. Uniqueness converges across regions: if two regions accept the same value concurrently, the document with the smallest id wins, and reading the losing document answers `409 unique_violation`.

`GET "$DB/schema/orders"` returns `uniqueConstraints` and the fields inferred from the first 1000 documents. It does not return the JSON Schema.

</div>
