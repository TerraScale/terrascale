---
title: Export your data
description: Stream every live document of a TerraBase database as NDJSON, resume an interrupted export and restore it by writing each document back.
---

# Export your data

`GET /v1/projects/{project}/databases/{database}/export` streams every live document of one database as newline-delimited JSON (`application/x-ndjson`). Use it to keep a copy of your data outside TerraScale, to move documents into another database or to feed your own tools. To export the end-user accounts of a project's sign-in, see [Export the directory](/guides/end-user-authentication#export-the-directory).

::: info Rolling out
Export is rolling out with the current TerraBase release. If the route answers `404` for a database your token can read, your serving release does not provide it yet. The JavaScript `export()` method ships in `@terrascale/sdk` 0.0.5; `0.0.4` and older do not have it.
:::

## What an export contains

Each line is one JSON object for one live document:

```json
{"collection":"orders","id":"01JC9Q5Y6Z2K8V7N3M4P5R6S7T","document":{"total":42,"status":"paid"}}
```

- **Every live document, nothing else.** Deleted documents are not exported. Collections, indexes, schemas and settings are not part of the stream.
- **Ordered** by collection name, then document id.
- **One snapshot.** One request reads the whole database from a single read snapshot, so writes made while the stream runs do not appear in it.
- **Complete only when the response ends cleanly.** The response is chunked. A stream that stops early, such as after a connection reset, is incomplete. A line cut in half is never a document: drop it and [resume](#resume-an-interrupted-export) after the last whole line.

## Before you start

You need a project access token with `documents:read`. A missing or invalid token answers `401`, a missing scope `403`, and a database the token does not reach `404 unknown_database`. See [authentication](/guides/authentication). Keep the token in your secret manager, and treat the export file as customer data: it holds every document.

## Export with curl

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export PROJECT='YOUR_PROJECT_ID' DATABASE='orders'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }

api "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/export" > export.ndjson
echo "curl exit $?"; wc -l < export.ndjson
```

Exit code `0` means the stream ended cleanly. Any other code means the file is incomplete.

## Export with JavaScript

```js
import { createWriteStream } from 'node:fs'
import { pipeline } from 'node:stream/promises'
import { createTerraBaseV1Client } from '@terrascale/sdk'

const client = createTerraBaseV1Client({
  baseUrl: process.env.TERRASCALE_API_URL,
  project: process.env.PROJECT,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
})
const db = client.database('orders')
// Each line is { collection, id, document }; pipeline waits for the file to drain.
await pipeline(async function* () {
  for await (const line of db.export()) yield JSON.stringify(line) + '\n'
}, createWriteStream('export.ndjson'))
```

`export()` returns an async generator. It throws `TerraBaseV1Error` for a non-2xx answer and `incomplete export line` when the stream is cut. Pass `{ signal }` to cancel it.

## Export with .NET

```csharp
await using var file = File.CreateText("export.ndjson");
var database = client.Database(projectId, "orders");
await foreach (var line in database.ExportAsync(cancellationToken: ct))
{
    await file.WriteLineAsync(JsonSerializer.Serialize(new { collection = line.Collection, id = line.Id, document = line.Document }));
}
```

`client` is the `TerraBaseDocumentsClient` from the [C# quickstart](/getting-started/csharp). `ExportAsync` returns `IAsyncEnumerable<TerraBaseExportLine>`; each line has `Collection`, `Id`, `Document` (a `JsonElement`) and `Cursor`.

## Resume an interrupted export

Pass the collection and id of the last whole line you received. The export continues after that document.

```bash
# Drop a cut last line, make sure the file ends with a newline, then continue after the last whole line.
tail -n 1 export.ndjson | jq -e . > /dev/null 2>&1 || sed -i '$d' export.ndjson
[ -z "$(tail -c 1 export.ndjson)" ] || echo >> export.ndjson
last=$(tail -n 1 export.ndjson)
api -G "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/export" \
  --data-urlencode "after_collection=$(jq -r .collection <<< "$last")" \
  --data-urlencode "after_id=$(jq -r .id <<< "$last")" >> export.ndjson
```

On macOS, use `sed -i ''`. Run it again until `curl` exits with `0`.

```js
for await (const line of db.export({ after: { collection: last.collection, id: last.id } })) { /* … */ }
```

```csharp
await foreach (var line in database.ExportAsync(last.Cursor, ct)) { /* … */ }
```

The resumed part reads a **new** snapshot. Documents written, changed or deleted between the two requests can differ from the first part, so a resumed export is not one point-in-time copy. For a single consistent copy, run the export again from the start, or export a [branch](/guides/branches) or a [restored database](/guides/backups-and-restore) that nobody writes to.

## Restore an export

Create each collection in the destination database, then `PUT` each line's `document` to its collection and id:

```bash
DEST='orders-copy'
D="$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DEST"
jq -r .collection export.ndjson | uniq | while read -r c; do
  jq -n --arg name "$c" '{$name}' | api -H 'Content-Type: application/json' --data-binary @- "$D/collections" > /dev/null
done
while IFS= read -r line; do
  c=$(jq -r .collection <<< "$line") id=$(jq -r .id <<< "$line")
  jq -c .document <<< "$line" | api -X PUT -H 'Content-Type: application/json' --data-binary @- \
    "$D/collections/$c/documents/by-key?id=$id" > /dev/null
done < export.ndjson
```

The token needs `documents:write` on the destination database. A `PUT` replaces a document with the same id, so restore into a new or empty database. To restore with a dry-run preview and validation instead, upload `export.ndjson` as an [NDJSON import](/guides/import-ndjson). Document versions and history are not part of the export; restored documents get new versions. Writes count toward your plan's quotas and metering, see [pricing](/pricing/).

Scheduled customer-bucket exports are available: first configure the project’s [backup destination](/guides/backups-and-restore#your-own-backup-bucket), then `api -X PUT -H 'Content-Type: application/json' -d '{"cron":"0 3 * * *"}' "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/export-schedule"` (`databases:write`, organization writer). The five-field cron uses UTC; missing destination is `409 conflict`, invalid cron `400 invalid_request`. `GET` on the same path needs `databases:read` and returns `cron`, `updatedAt`, `lastRun` (null before the first attempt). Inspect `lastRun.status` (`succeeded`/`failed`), `startedAt`, `finishedAt`, `key`, `documents`, `bytes` and `error`; failures wait for the next cron occurrence. Each successful run writes the same snapshot NDJSON to `terrabase-exports/{project}/{databaseId}/{yyyyMMddTHHmmssZ}.ndjson`. Bucket credentials need write permission on that prefix in addition to backup permissions. Existing objects remain after database deletion; an export omits schema and history and is not a full backup.

## What is not offered

- CSV, Parquet or any format other than NDJSON.
- On-demand asynchronous export jobs.
- Collection definitions, indexes, schemas, document versions or deleted documents.
- A single snapshot across a resumed export.

For full-fidelity copies of a database, use [backups and restore](/guides/backups-and-restore) or [branches](/guides/branches).
