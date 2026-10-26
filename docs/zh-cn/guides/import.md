---
title: Import an existing database
description: One-time import of a PostgreSQL, Turso or NDJSON (MongoDB, Firestore) export into a new, empty TerraBase database, with full dry-run preview and validation.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Import an existing database

Export a PostgreSQL, Turso, MongoDB or Firestore database with your own tools, upload the file into a new, empty TerraBase database, review a dry-run preview of every row and start the copy.

In the dashboard, open **Databases** and select **Import**. The page uploads your file, shows the preview and starts the copy. The steps below do the same over the HTTP API. Rehearse with synthetic data before you import production data.

Prepare your export with its guide:

- [Export from PostgreSQL](/zh-cn/guides/import-postgresql)
- [Export from Turso](/zh-cn/guides/import-turso)
- [Import from MongoDB or Firestore (NDJSON)](/zh-cn/guides/import-ndjson)

## What an import is

- **One-time copy of an export file.** You export on your own machine and upload one file of at most 1 GiB. There is no ongoing sync or change data capture. Writes made to the source after the export are not imported.
- **No access to your source.** TerraBase never connects to your source database and never receives its credentials.
- **Nothing in the export is executed.** TerraBase reads table definitions and row data with its own parser; no SQL in the file runs. Objects that hold no data are listed as skipped. Anything that can hold data and would not be imported blocks the import instead of being dropped silently.
- **Dry run of every row first.** Analysis maps every row before anything is written, so invalid values, IDs and oversized documents appear in the preview. No document is written until you start the copy with that exact preview.
- **Isolated destination.** You create a new database for each import. The import attaches only to a database without collections, writes each document create-only and never overwrites a document it did not write. TerraBase never deletes the destination on its own.

## Before you start

- A project access token for this project with `databases:write`, `documents:write` and `databases:read`. A request without one, including a legacy API key, answers `401`. A token for another project or narrowed to another database answers `404`. See [authentication](/zh-cn/guides/authentication).
- An export file prepared with the [PostgreSQL](/zh-cn/guides/import-postgresql), [Turso](/zh-cn/guides/import-turso) or [NDJSON](/zh-cn/guides/import-ndjson) guide.
- `bash` or `zsh`, `curl` 7.76 or newer, and `jq`.
- Room in your plan. The destination counts as one of your plan's databases. Imported documents count toward your organization's storage quota and are metered as document writes; import calls, upload bytes included, are metered as requests. An import never bypasses a quota. See [pricing](/zh-cn/pricing/).

`@terrascale/sdk` 0.0.4 wraps these routes as `client.database(name).imports`. The steps below use the HTTP routes directly.

## Keep credentials and the export private

- Never send a source password, token or connection string to TerraScale, including in support requests. TerraBase does not need them.
- Keep the TerraScale token in your secret manager. The commands below pass it to `curl` through a file descriptor, so it does not appear in arguments or shell history.
- The export file contains all of your data. Restrict it with `chmod 600`, keep it out of chats, tickets, repositories and shared drives, and delete it when you accept the import.
- Preview, progress and error responses contain table names, keys and values. Treat them as customer data.

**Retention of the uploaded file.** TerraBase stores the upload in the destination's region and deletes it:

- When the copy completes, or when you delete the destination or its `terrascale-import` record.
- When an incomplete upload has been idle for 1 day. The job stays in `awaiting_source`, and the upload restarts at offset 0.
- When a preview (`ready` or `rejected`) has been left alone for 7 days. **Pending:** how to continue such a job. Until that is documented, start or cancel within 7 days.
- 7 days after the job failed or was cancelled (`source.retainedUntil`). A later upload extends this.

## Import step by step

Run these in one shell session from the directory that holds your export.

1. Set the connection and helper functions:

   ```bash
   export TERRASCALE_API_URL='https://YOUR_AUTHORIZED_API_HOST'
   export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
   # TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
   SOURCE=dump.sql FORMAT=postgres_plain DB="import-$(date -u +%Y%m%d-%H%M%S)" REGION=
   P="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID"
   D="$P/databases/$DB"
   headers() {
     printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN"
     [ -z "$REGION" ] || printf 'X-TerraScale-Region-Pin: %s\n' "$REGION"
   }
   api() { curl -sS --fail-with-body -H @<(headers) "$@"; }
   job() { api "$D/import" > job.json && jq -r .state job.json; }
   echo "$DB"
   ```

   Use `FORMAT=sqlite` for a Turso or SQLite file, and `FORMAT=ndjson` for an [NDJSON](/zh-cn/guides/import-ndjson) file. `DB` is a fresh name for the new destination: a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63 characters. Note `DB` and, after step 2, `REGION`, and set both again if you continue in another shell. On macOS, replace `sha256sum` in the next step with `shasum -a 256`.

2. Create the destination database, then attach the import to it. Declaring the exact size and SHA-256 binds the import to this file.

   ```bash
   REGION=$(jq -n --arg name "$DB" '{$name}' |
     api -H 'Content-Type: application/json' --data-binary @- "$P/databases" | jq -r .homeRegion)
   jq -n --arg format "$FORMAT" \
     --argjson sourceBytes "$(wc -c < "$SOURCE")" \
     --arg sourceSha256 "$(sha256sum "$SOURCE" | cut -d ' ' -f 1)" \
     '{$format, $sourceBytes, $sourceSha256}' |
     api -H 'Content-Type: application/json' --data-binary @- "$D/import" | jq .state
   ```

   The state is `awaiting_source`. Import runs in the destination’s home region and supports replicated destinations; keep the placement returned by database creation. Repeating the identical attach request returns the existing job. On `409 name_taken`, verify ownership and the existing import before continuing; a name collision alone does not identify your database.

3. Upload the file in parts of at most 4 MiB, then wait for analysis:

   ```bash
   size=$(( $(wc -c < "$SOURCE") )) sent=-1
   # Stops when the server's offset does not advance, such as after a checksum mismatch.
   while offset=$(api "$D/import" | jq -e .source.receivedBytes) &&
         [ "$offset" -gt "$sent" ] && [ "$offset" -lt "$size" ]; do
     sent=$offset
     tail -c +$((offset + 1)) "$SOURCE" | head -c 4194304 |
       api -X PUT -H 'Content-Type: application/octet-stream' --data-binary @- \
         "$D/import/source?offset=$offset" > /dev/null || break
   done
   while state=$(job) && [ "$state" = analyzing ]; do sleep 2; done
   jq '{state, error: .error.code, blocking: .preview.blocking, warnings: .preview.warnings}' job.json
   ```

   Each part continues from the job's `receivedBytes`, so if the upload stops, run this step again. The state becomes `ready`, or `rejected` when the preview has blocking issues or the file is unsupported. If it returns to `awaiting_source` with `source_checksum_mismatch`, the received bytes did not match the declared SHA-256 and were discarded: run this step again with the same file. If that repeats, the file changed after step 2; create a new database and import for it.

4. Review the [preview](#review-the-preview):

   ```bash
   jq '.preview.tables[] | {source, collection, excluded, rows, issues}' job.json
   jq '.preview.skipped' job.json
   ```

5. Resolve blocking issues by changing [options](#options) and analyzing again. Skip this step when `blocking` is `0`.

   ```bash
   jq -n '{options: {exclude: ["public.orders.tags"]}}' |
     api -X PATCH -H 'Content-Type: application/json' --data-binary @- "$D/import" | jq .state
   while state=$(job) && [ "$state" = analyzing ]; do sleep 2; done
   ```

   Each analysis produces a new `preview.id`. Repeat steps 4 and 5 until `blocking` is `0` and you accept every mapping and warning.

6. Start the copy with the preview you reviewed, then watch it:

   ```bash
   jq '{previewId: .preview.id}' job.json |
     api -H 'Content-Type: application/json' --data-binary @- "$D/import/start" | jq .state
   while state=$(job) && [[ $state =~ ^(queued|running|validating)$ ]]; do
     jq -c .progress job.json; sleep 2
   done
   jq '{state, error, validation}' job.json
   ```

   The `preview.id` binds the copy to the uploaded file's SHA-256, your options and the complete mapping in that preview. The copy applies exactly that stored plan, and `confirmedPreviewId` records what you confirmed.

7. Accept the import only when `state` is `completed` and `validation.passed` is `true`. Point a test copy of your application at the destination before you switch production traffic.

Do not write to the destination before the import completes. A document the import did not write stops the copy with `destination_modified`.

The import runs in the destination's home region. After step 2, `api` pins every call to it with `X-TerraScale-Region-Pin`. A call that reaches another region answers `421 wrong_region` with an `X-TerraBase-Database-Home` header naming the home region.

## Review the preview

| Field | Meaning |
| --- | --- |
| `tables[]` | One plan per source table: `source`, target `collection`, `excluded`, `rows`, `primaryKey`, `columns`, `constraints` and `issues`. An unnamed constraint has `name` `null`. |
| `columns[].mapping` | How values become JSON; see the [PostgreSQL](/zh-cn/guides/import-postgresql#type-mapping) and [Turso](/zh-cn/guides/import-turso#type-mapping) mappings |
| `issues[]` | `blocking` issues stop the import; warnings are for you to accept. A value issue carries its `row`. At most 50 per table. |
| `skipped[]` | Objects that are listed and never imported or run, such as functions, views, indexes, sequences, grants and SQLite internal tables. At most 200 entries; `skippedTruncated` marks more. |
| `blocking`, `warnings` | Issue totals. `startImport` is refused while `blocking` is above `0`. |

A preview can cover at most 1000 tables and 1600 columns per table. A preview larger than 512 KiB of JSON is `rejected` with `preview_too_large`; exclude tables or split the export.

### Options

| Option | Default | Effect |
| --- | --- | --- |
| `exclude` | `[]` | Up to 1000 `"schema.table"` or `"schema.table.column"` entries to leave out. A SQLite or Turso schema is `main`. |

Write a name bare when it uses only `a-z`, `0-9` and `_` and does not start with a digit. Otherwise double-quote it, doubling any `"` inside, as in SQL: `public."Order Items"."unit.price"`. PostgreSQL names match exactly; SQLite names match without regard to case.

## How data maps

NDJSON files map line by line, as described in [NDJSON](/zh-cn/guides/import-ndjson#how-lines-become-documents). For PostgreSQL and SQLite:

- **Collection:** the table name, lowercased. Tables in `public` (PostgreSQL) or `main` (SQLite) drop the schema; others become `schema_table`. Characters outside `a-z`, `0-9`, `_` and `-` become `_`, a name that does not start with a letter gets a `t_` prefix, and names are cut to 63 characters. Two tables that map to one name, or a reserved name such as `collections`, block the import (`collection_name_collision`).
- **Document ID:** a deterministic ULID for each row. Its time is the job's creation time, and its randomness is the SHA-256 of the job ID, table and row number, so every resume writes the same IDs. The primary key and `rowid` are not used. Primary key columns stay ordinary document fields.
- **Document body:** one JSON member per imported column, named exactly as the column.
- **Constraints are recorded, never enforced.** Primary, unique and foreign keys and `NOT NULL` appear in the preview with `enforced: false`; values are imported unchanged. A foreign key holds the referenced row's primary key field value, not its document ID. Check constraints, defaults, sequences, indexes, triggers, views, functions, policies, extensions, ownership and grants are skipped.
- **Import record.** The destination keeps a collection named `terrascale-import` as the import's audit record. Deleting that collection makes the database forget its import.
- **Users, roles and passwords** from the source are never imported. Set up access with [authentication](/zh-cn/guides/authentication).

## Job states

```text
awaiting_source → analyzing → ready → queued → running → validating → completed
       ↑               ↘ rejected                   ↘ failed / cancelled
       └─ checksum mismatch
```

| State | What it means | What you can do |
| --- | --- | --- |
| `awaiting_source` | Waiting for the file, or a damaged upload was discarded | Upload the declared file, or cancel |
| `analyzing` | Reading and mapping every row | Wait, or cancel |
| `ready` | Preview has no blocking issues | Start, change options, or cancel |
| `rejected` | Preview has blocking issues, or the file is unsupported | Change options, or cancel |
| `queued`, `running` | Waiting for capacity, then copying documents | Watch `progress`, or cancel |
| `validating` | Comparing counts and checksums | Wait, or cancel |
| `completed` | Validated | Accept the import |
| `failed`, `cancelled` | Stopped; documents already written stay | Resume while the destination exists |

Repeating a call whose effect already holds answers `202` with the current job, so start, cancel and resume are safe to retry. Any other disallowed transition answers `409 invalid_state`. `Idempotency-Key` is not supported.

The source of an import never changes. To import a corrected or re-exported file, create a new database and a new import.

## Progress, cancellation and resume

- **Bounded streaming.** The file is read sequentially. Each batch of at most 500 documents and 1 MiB is written in one transaction together with the job's checkpoint. `progress` reports tables and rows done.
- **Cancel** with `api -X POST "$D/import/cancel" | jq .state`. A copy stops before its next batch, within seconds. Documents already written stay in the destination.
- **Resume** a `failed` or `cancelled` job with `api -X POST "$D/import/resume" | jq .state`. If you had started the copy, it continues after the last committed batch under the same confirmed preview (`confirmedPreviewId`). If you had not, the job analyzes again and you start it with the new preview. When `source.staged` is `false`, resume answers `409 source_required`: upload the same file again with step 3 first.
- **Retries.** TerraBase retries storage pressure and capacity refusals with backoff, then fails with `write_failed` and `retryable: true`. A data error fails at once with `retryable: false` and reports its `table`, `row` and `column`.

## Validation

When the copy finishes, the job compares, for each table:

- The number of rows in the export with the number of documents in the collection.
- An order-independent checksum of the mapped documents with the same checksum read back from the destination: SHA-256 over each ID and canonical body, summed.

`validation.tables[]` reports both sides per table. Any mismatch fails the job with `validation_failed`. Validation proves the destination matches the export; it cannot detect writes your source received after you exported. Also spot-check edge values yourself, such as the largest integers, exact decimals, time zones, binary columns, `NULL` and empty strings.

## Errors and recovery

| Response or `error.code` | What to do |
| --- | --- |
| `401` | Use a project access token; legacy API keys are not accepted. Do not retry automatically. |
| `403 forbidden_scope` | Use a token with the required scopes. |
| `404 unknown_database` | Check `DB` and that the token belongs to this project and is not narrowed to another database. |
| `409 destination_not_empty` | The database already has collections. Create a new database for the import. |
| `409 import_exists` | The database already has a different import. Use a new database. |
| `409 offset_mismatch` | Run step 3 again; it continues from `receivedBytes`. |
| `409 preview_changed`, `409 preview_blocked` | Fetch the job, review the current preview, resolve blocking issues, then start with its `preview.id`. |
| `409 source_required` | Upload the same file again with step 3, then resume. |
| `413 payload_too_large` | The file is over 1 GiB. Export fewer tables into separate imports. |
| `421 wrong_region` | Set `REGION` to the `X-TerraBase-Database-Home` value and repeat. |
| `429 tenant_busy`, `429 tenant_memory`, `503 capacity`, `503 unavailable` | Wait, then retry the same call. |
| `unsupported_source_format`, `wal_mode_source`, `unsupported_statement`, `malformed_source` | Re-export as your source guide describes, then import into a new database. |
| `unsupported_type`, `invalid_value`, `document_too_large` | Change [options](#options): exclude the reported column or table. Or fix the source and import a new export into a new database. |
| `destination_modified` | Something else wrote to the destination; the message names the cause. Delete the destination, resolve it, then import into a new database. |
| `quota_exceeded` | Resume after your plan allows more. |
| `preview_changed` as the job's error | TerraBase's mapping changed after you confirmed; the job is `analyzing` again. Review the new preview and start with its `preview.id`. |
| `write_failed` with `retryable: true` | Resume. |
| `validation_failed` | Do not use the destination. Compare `validation.tables`, then import again into a new database. |

A rejected, failed or cancelled import never changes your source.

## Find and delete a destination

There is no import list. List your databases, then fetch each one's import; a database without an import answers `404 unknown_import`.

Deleting a database removes all of its documents, its import job and its staged upload. It cannot be undone. Confirm that `DB` names the import destination first:

```bash
api "$D/import" | jq '{database, state, sourceSha256: .source.sha256}'
api -X DELETE "$D"
```

## Limits

- 1 GiB per export file, uploaded in parts of at most 4 MiB.
- At most 1 GiB of uploads staged at once per organization, counting incomplete uploads at their declared size. More answers `503 capacity`.
- 1000 tables per import and 1600 columns per table; a SQLite schema of at most 20000 objects or 16 MiB of SQL.
- Each document: 1 MiB, 16 levels of nesting and 4096 values.
- PostgreSQL: 16 MiB per line in the dump.
- Analyses and copies are queued. A started import can wait in `queued`, and a busy region answers `503 capacity` until there is room.
- The first data error stops the copy; there is no row-level skip.
- **Pending:** copy throughput on the hosted service. Plan for an import of millions of rows to take hours.

Not supported in contract v1: direct connections to a source database, ongoing sync or change data capture, importing into a database that holds data, enforcing constraints, PostgreSQL arrays and composite types, and one collection per partitioned table.

</div>
