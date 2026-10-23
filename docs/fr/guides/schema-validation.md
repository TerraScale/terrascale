---
title: Schema validation and unique keys
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Schema validation and unique keys

Collections start schemaless. Two separate routes add rules to a collection:

| Route | Purpose | Scope |
| --- | --- | --- |
| `…/collections/{collection}/schema` | A JSON Schema that every written document must satisfy | `documents:write` (`documents:read` to read) |
| `…/schema/{collection}` | Unique keys, plus the fields inferred from stored documents | `databases:write` (`databases:read` to read) |

No SDK wraps these routes yet. The examples use `curl` and `jq`.

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

## What happens on an invalid write

Creates, puts, both patch formats and [transactions](/fr/guides/transactions) validate the complete resulting document. A failing write answers `400 schema_validation_failed` and changes nothing:

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
