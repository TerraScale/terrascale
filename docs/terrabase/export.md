# Exporting a database as NDJSON

`GET /v1/projects/{project}/databases/{database}/export` streams every live document of a database as
`application/x-ndjson`, one object per line:

```json
{"collection":"items","id":"01JC9Q5Y6Z2K8V7N3M4P5R6S7T","document":{"name":"Ada"}}
```

- Scope: `documents:read`. A database the key does not reach answers `404 unknown_database`.
- Order: collection name, then document id. Deleted documents are not exported.
- Consistency: one request reads one snapshot (a single read transaction for the whole stream).
- Resume: pass `after_collection` and `after_id` from the last line received. The export continues after that
  document; the resumed part reads a new snapshot.
- Completeness: the response is chunked. A stream that ends without its final chunk (connection reset) is incomplete;
  resume it from the last whole line.

```sh
curl -sS -H "Authorization: Bearer $TOKEN" \
  "https://api.terrascale.tech/v1/projects/$PROJECT/databases/$DATABASE/export" > export.ndjson
wc -l export.ndjson
```

To restore an export, `PUT` each line's `document` to
`/v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key?id={id}` after creating the
collections.

Not provided: CSV or Parquet output, and asynchronous export to a bucket.
