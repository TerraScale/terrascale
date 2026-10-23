---
title: Import from MongoDB or Firestore (NDJSON)
description: Export MongoDB collections with mongoexport or Firestore collections with a short Node.js script into one NDJSON file, then run the one-time import into a new TerraBase database.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Import from MongoDB or Firestore (NDJSON)

Write your documents to one NDJSON file (one JSON object per line), check it and upload it with the [import steps](/ar/guides/import#import-step-by-step) using `FORMAT=ndjson`. The same format also imports a TerraBase [export](/ar/guides/export).

::: info
The commands on this page run on your own machine. They were checked with MongoDB 8 and its `mongoexport`, and with `firebase-admin` 14.5 against the Firestore emulator; confirm flags with `--help` for your version. TerraBase never connects to your database and never receives its credentials.
:::

## How lines become documents

| Line | Collection | Document ID | Document body |
| --- | --- | --- | --- |
| Exactly `{"collection", "id", "document"}`, as written by [export](/ar/guides/export) | `collection` | `id`, which must be a ULID | `document` |
| Any other JSON object | `options.collection` when set, else the line's `collection` string, which is removed from the body | A new ULID | The rest of the line |

- **Source IDs stay fields.** A MongoDB `_id`, a Firestore document ID or any `id` member becomes an ordinary field of the document. Each new ULID is deterministic: a resumed copy writes the same IDs.
- **Collection names** start with a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63 characters. Any other name blocks that collection with `invalid_collection_name`.
- **Values are stored as written.** MongoDB Extended JSON wrappers such as `{"$oid": …}` and `{"$date": …}` stay objects unless you [flatten them](#optional-flatten-extended-json) before the upload.
- **Lines:** blank lines are skipped, `\r\n` is accepted and one line holds at most 16 MiB. A line that is not a JSON object, or names no collection, rejects the file with `malformed_source` and the line's byte offset.
- **Preview:** each collection appears as table `main.<collection>`. Leave one out with the [option](/ar/guides/import#options) `exclude: ["main.<collection>"]`.

Not supported: JSON arrays (`mongoexport --jsonArray`), CSV, and ongoing sync from the source.

## 1. Rehearse with synthetic data

Save this as `export.ndjson` and run the [import steps](/ar/guides/import#import-step-by-step) with `SOURCE=export.ndjson FORMAT=ndjson`:

```json
{"collection":"users","_id":{"$oid":"6720f1a2b3c4d5e6f7a8b9c0"},"email":"ada@example.com","createdAt":{"$date":"2026-01-31T23:59:59Z"}}
{"collection":"users","_id":{"$oid":"6720f1a2b3c4d5e6f7a8b9c1"},"email":"lin@example.com","tags":["beta"]}
{"collection":"orders","_id":"o-1","user":"6720f1a2b3c4d5e6f7a8b9c0","total":19.99}
```

Expect a preview with tables `main.users` (2 rows) and `main.orders` (1 row) and no blocking issues. After the copy, each document holds its `_id` as a field and has a new ULID as its document ID.

## 2a. Export from MongoDB

Put the connection string in a file instead of the command line, so it stays out of process lists and shell history. Create `mongo.yaml` with your editor, with this one line, then run `chmod 600 mongo.yaml`:

```yaml
uri: mongodb+srv://USER:PASSWORD@YOUR_CLUSTER/shop
```

Export each collection and tag its lines with the target collection, lowercased. This needs `jq` 1.7 or newer; older versions round integers above 2<sup>53</sup>.

```bash
(
  set -o pipefail
  for c in users orders; do
    mongoexport --config mongo.yaml --collection "$c" --quiet |
      jq -c --arg c "$(tr '[:upper:]' '[:lower:]' <<< "$c")" \
        'if has("collection") then error("\($c): a document has a collection field") else . + {collection: $c} end' ||
      exit 1
  done
) > export.ndjson && echo 'export complete'
chmod 600 export.ndjson
```

`mongoexport` writes relaxed Extended JSON, one document per line. Without `export complete`, the file is incomplete: the loop stops at the first failed export or at a document that already has a `collection` field, because the import would read that field as its collection; to keep that field under another name, replace the `jq` filter with `'. + {source_collection: .collection, collection: $c}'`. To import a single collection, skip the `jq` step and set `options.collection` instead, as shown in [step 3](#_3-import-the-file).

`mongoexport` reads a live collection without a snapshot across collections. Stop writes to the source while you export, or accept that documents changed during the export may differ. Indexes, validation rules, users and roles are not exported; recreate indexes with [indexes](/ar/guides/indexes).

### Optional: flatten Extended JSON

Turn object IDs, dates and decimals into plain strings, so you can filter and sort on them like other fields:

```bash
jq -c 'walk(if type == "object" and length == 1
  then (.["$oid"] // .["$date"] // .["$numberDecimal"] // .) else . end)' export.ndjson > flat.ndjson
```

Then upload `flat.ndjson`. Dates outside the years 1970 to 9999 stay `{"$numberLong": …}` objects. Binary data, regular expressions and other wrappers stay objects.

## 2b. Export from Firestore

`gcloud firestore export` writes a backup format that only Firestore reads, so use this Node.js script instead. It writes every document of every collection and subcollection as one NDJSON line. Install `firebase-admin` in an empty directory with `npm install firebase-admin`, then save this as `export-firestore.mjs`:

```js
import { createWriteStream } from 'node:fs'
import { once } from 'node:events'
import { initializeApp } from 'firebase-admin/app'
import { getFirestore, Timestamp, GeoPoint, DocumentReference } from 'firebase-admin/firestore'

initializeApp() // Application Default Credentials; GOOGLE_CLOUD_PROJECT names the project.
const db = getFirestore()
const out = createWriteStream('export.ndjson', { mode: 0o600 })
const seen = new Map()

// users/u1/orders becomes users_orders; two paths that lowercase to one name stop the export.
function collectionFor(ref) {
  const group = ref.path.split('/').filter((_, i) => i % 2 === 0).join('/')
  const name = group.replaceAll('/', '_').toLowerCase()
  if ((seen.get(name) ?? group) !== group) throw new Error(`${group} and ${seen.get(name)} both map to ${name}`)
  seen.set(name, group)
  return name
}

const plain = (v) =>
  v instanceof Timestamp ? v.toDate().toISOString()
  : v instanceof GeoPoint ? { latitude: v.latitude, longitude: v.longitude }
  : v instanceof DocumentReference ? v.path
  : Buffer.isBuffer(v) ? v.toString('base64')
  : typeof v?.toArray === 'function' ? v.toArray() // vector
  : Array.isArray(v) ? v.map(plain)
  : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, plain(x)]))
  : v

async function exportCollection(ref) {
  const collection = collectionFor(ref)
  for await (const doc of ref.stream()) {
    const data = doc.data()
    for (const key of ['collection', '_id', '_path']) {
      if (key in data) throw new Error(`${doc.ref.path} has a field named ${key}`)
    }
    const line = JSON.stringify({ ...plain(data), _id: doc.id, _path: doc.ref.path, collection })
    if (!out.write(line + '\n')) await once(out, 'drain')
    for (const sub of await doc.ref.listCollections()) await exportCollection(sub)
  }
}

for (const ref of await db.listCollections()) await exportCollection(ref)
out.end()
await once(out, 'finish')
```

Sign in with `gcloud auth application-default login` with an account that can read the database, then run it:

```bash
GOOGLE_CLOUD_PROJECT=YOUR_FIREBASE_PROJECT node export-firestore.mjs
```

What the script writes:

- **Document ID and path** in the fields `_id` and `_path`. Subcollection documents keep their parent in `_path`, such as `users/u1/orders/o7`.
- **Timestamps** as ISO 8601 strings in UTC, with millisecond precision.
- **Geopoints** as `{"latitude", "longitude"}`, **references** as their document path, **bytes** as base64 strings and **vectors** as arrays of numbers.
- **NaN and Infinity** as `null`, because JSON has no such numbers. Integers above 2<sup>53</sup> − 1 lose precision, as in any JavaScript reader.
- **Collection names** joined along the path and lowercased. A Firestore name with other characters, such as `.`, blocks its collection with `invalid_collection_name`; rename it in `collectionFor`.

The script reads live data, not a snapshot. Stop writes while it runs, or accept that documents changed during the export may differ. It does not visit subcollections of a document that does not exist itself (shown in italics in the Firebase console); export those by passing their collection reference, such as `db.collection('users/u1/orders')`, to `exportCollection`. The script lists each document's subcollections with one extra request per document. Security rules and indexes are not exported. Every document read is billed by Firestore.

## 3. Import the file

Check that every line is a JSON object, and count the lines per collection:

```bash
jq -r '.collection' export.ndjson | sort | uniq -c
```

Run the [import steps](/ar/guides/import#import-step-by-step) with `SOURCE=export.ndjson FORMAT=ndjson`. If your lines have no `collection` member, such as a single `mongoexport` without the `jq` step, name the collection when you attach the import in step 2:

```bash
jq -n --arg format "$FORMAT" \
  --argjson sourceBytes "$(wc -c < "$SOURCE")" \
  --arg sourceSha256 "$(sha256sum "$SOURCE" | cut -d ' ' -f 1)" \
  '{$format, $sourceBytes, $sourceSha256, options: {collection: "users"}}' |
  api -H 'Content-Type: application/json' --data-binary @- "$D/import" | jq .state
```

`options.collection` places every line that is not an export line, even one with a `collection` member, which then stays a field. A later `PATCH` of options replaces all of them, so repeat `collection` next to `exclude`: `{options: {collection: "users", exclude: [...]}}`.

When the import is `completed` with `validation.passed`, look up a document by its old ID with a [query](/ar/guides/queries) on `_id`. Delete `mongo.yaml` and the export file once you accept the import.

## Errors

| `error.code` | Cause and fix |
| --- | --- |
| `malformed_source` | A line is not valid JSON, is not an object, names no collection, or is an export line whose `id` is not a string. The message gives the line's byte offset; see below. |
| `invalid_collection_name` | Rename the collection to lowercase letters, digits, `_` or `-`, starting with a letter. |
| `invalid_document_id` | An export line's `id` is not a ULID. Unwrap the lines so the import generates IDs: `jq -c '{collection, source_id: .id} + .document'`. |
| `document_too_large` | A document is over 1 MiB, 16 levels of nesting or 4096 values. Exclude its collection, or split the document in the source. |
| `duplicate_document_id` | Two export lines share a collection and ID. Remove the duplicate line. |

Print the line at a reported byte offset, such as 1234:

```bash
tail -c +$((1234 + 1)) export.ndjson | head -n 1
```

Fix the file, then import it into a new database; the source of an import never changes. Other errors are listed in the [import guide](/ar/guides/import#errors-and-recovery).

</div>
