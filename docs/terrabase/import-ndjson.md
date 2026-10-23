# Importing NDJSON documents

`format: "ndjson"` runs an NDJSON file through the same import flow as PostgreSQL and SQLite sources
([import](import.md)): create, upload, preview, start, resume, cancel and validation are unchanged.

```json
{"format":"ndjson","sourceBytes":1234,"sourceSha256":"…","options":{"collection":"users"}}
```

## Lines

One JSON object per line; blank lines are skipped, `\r\n` is accepted, a line is at most 16 MiB.

| Line | Collection | Document id | Body |
|---|---|---|---|
| Export line `{"collection","id","document"}` (exactly these three members, [export](export.md)) | `collection` | `id` | `document` |
| Any other object | `options.collection` when set; else the line's string `collection` member, which is removed | `id`, else `_id`, which is removed | the rest of the line |

- Ids: a string is used as it is, a number as its JSON text, a MongoDB `{"$oid": "…"}` as its hex. A line with no id
  gets a deterministic ULID (the job's time, randomness from the job, the collection and the line's position in it), so a resumed copy writes
  the same ids. Ids follow the Documents API rule: 1 to 256 characters without control characters.
- `options.collection` must be a valid, unreserved collection name. Export lines keep their own collection.
- Other MongoDB Extended JSON wrappers (`$date`, `$numberLong`, …) are stored as the objects they are.
- Each collection previews as table `main.<collection>` with `idPolicy` `document`; `exclude: ["main.<collection>"]`
  leaves it out.

## Refusals

- A line that is not a JSON object, names no collection (no `collection` member and no `options.collection`) or has an
  id that is not a string, number or `{"$oid"}` rejects the source (`malformed_source`, with the line's byte offset).
- An invalid collection name blocks that collection (`invalid_collection_name`); an invalid document id or body blocks
  as for other formats (`invalid_document_id`, `invalid_value`, `document_too_large`).
- Two lines with the same collection and id fail the copy (`duplicate_document_id` or `destination_modified`).

## Round trip

```sh
curl -sS -H "Authorization: Bearer $TOKEN" "$API/v1/projects/$PROJECT/databases/$SOURCE/export" > export.ndjson
# then run the import flow on a new database with {"format":"ndjson", …} and upload export.ndjson
```

The new database exports the same lines, plus the import's own `terrascale-import` record collection.

Not provided: CSV, JSON arrays (`mongoexport --jsonArray`), live sync from MongoDB.
