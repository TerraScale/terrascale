---
title: Import an existing database
description: Proposed one-time import of a PostgreSQL or Turso export into a new, isolated TerraBase database, with dry-run preview and validation.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

# Import an existing database

Export a PostgreSQL or Turso database with your own tools, upload the file, review a dry-run preview and import it into a new, isolated TerraBase database.

::: warning Pending feature
Import follows the proposed import contract v1. Its implementation is in progress and no serving release provides it yet. Routes, fields and limits on this page can change before release. Rehearse with synthetic data only; do not upload production data until import is announced for your release.
:::

Prepare your export with its guide:

- [Export from PostgreSQL](/guides/import-postgresql)
- [Export from Turso](/guides/import-turso)

## What an import is

- **One-time copy of an export file.** You export on your own machine and upload one file of at most 1 GiB. There is no ongoing sync or change data capture. Writes made to the source after the export are not imported.
- **No access to your source.** TerraBase never connects to your source database and never receives its credentials.
- **Nothing in the export is executed.** TerraBase reads table definitions and row data. Every other statement or object is listed as skipped.
- **Dry run first.** Uploading produces a preview. No document is written until you start the import with that exact preview.
- **Isolated destination.** Each import creates its own new database in one region, without replicas. It never writes into an existing database, and TerraBase never deletes the destination on its own.

## Before you start

- A project access token with `databases:write`, `documents:write` and `databases:read`. See [authentication](/guides/authentication).
- An export file prepared with the [PostgreSQL](/guides/import-postgresql) or [Turso](/guides/import-turso) guide.
- `bash` or `zsh`, `curl` 7.76 or newer, and `jq`.
- Room for one more database in your plan. **Pending:** how a destination counts toward plan allowances, and how imported writes and storage are metered. See [pricing](/pricing/).

No SDK or CLI wraps import in contract v1. The steps below use its public HTTP routes. **Pending:** a guided dashboard flow.

## Keep credentials and the export private

- Never send a source password, token or connection string to TerraScale, including in support requests. TerraBase does not need them.
- Keep the TerraScale token in your secret manager. The commands below pass it to `curl` through a file descriptor, so it does not appear in arguments or shell history.
- The export file contains all of your data. Restrict it with `chmod 600`, keep it out of chats, tickets, repositories and shared drives, and delete it when you accept the import.
- TerraBase keeps the uploaded file in the destination's region until the job completes or you upload again. After a failed or cancelled job it keeps the file for 7 days (`source.retainedUntil`).
- Preview, progress and error responses contain table names, keys and values. Treat them as customer data.

## Import step by step

Run these in one shell session from the directory that holds your export.

1. Set the connection and helper functions:

   ```bash
   export TERRASCALE_API_URL='https://YOUR_AUTHORIZED_API_HOST'
   export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
   # TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
   SOURCE=dump.sql FORMAT=postgres_plain DB=shop-import
   P="$TERRASCALE_API_URL/v1/projects/$TERRASCALE_PROJECT_ID"
   D="$P/databases/$DB"
   auth() { printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN"; }
   api() { curl -sS --fail-with-body -H @<(auth) "$@"; }
   job() { api "$D/import" > job.json && jq -r .state job.json; }
   ```

   Use `FORMAT=sqlite` for a Turso or SQLite file. `DB` names the new destination database. On macOS, replace `sha256sum` in the next step with `shasum -a 256`.

2. Create the import. Declaring the exact size and SHA-256 binds the job to this file.

   ```bash
   jq -n --arg format "$FORMAT" --arg database "$DB" \
     --argjson sourceBytes "$(wc -c < "$SOURCE")" \
     --arg sourceSha256 "$(sha256sum "$SOURCE" | cut -d ' ' -f 1)" \
     '{$format, $database, $sourceBytes, $sourceSha256}' |
     api -H 'Content-Type: application/json' --data-binary @- "$P/imports" | jq .state
   ```

   The state is `awaiting_source`. A repeated create answers `409 name_taken`. Check that job's `source.sha256` with `job` before you assume it is yours.

3. Upload the file, then wait for analysis:

   ```bash
   api -T "$SOURCE" -H 'Content-Type: application/octet-stream' "$D/import/source" | jq .state
   while state=$(job) && [ "$state" = analyzing ]; do sleep 5; done
   jq '{state, blocking: .preview.blocking, warnings: .preview.warnings}' job.json
   ```

   The state becomes `ready`, or `rejected` when the preview has blocking issues or the file is unreadable.

4. Review the [preview](#review-the-preview):

   ```bash
   jq '.preview.tables[] | {source, collection, rows, idPolicy, issues}' job.json
   jq '.preview.skipped' job.json
   ```

5. Resolve blocking issues by changing [options](#options) and analyzing again. Skip this step when `blocking` is `0`.

   ```bash
   jq -n '{options: {missingPrimaryKey: "generate", exclude: ["public.orders.tags"]}}' |
     api -X PATCH -H 'Content-Type: application/json' --data-binary @- "$D/import" | jq .state
   while state=$(job) && [ "$state" = analyzing ]; do sleep 5; done
   ```

   Each analysis produces a new `preview.id`. Repeat steps 4 and 5 until `blocking` is `0` and you accept every mapping and warning.

6. Start the import with the preview you reviewed, then watch it:

   ```bash
   jq '{previewId: .preview.id}' job.json |
     api -H 'Content-Type: application/json' --data-binary @- "$D/import/start" | jq .state
   while state=$(job) && [[ $state =~ ^(queued|running|validating)$ ]]; do
     jq -c .progress job.json; sleep 10
   done
   jq '{state, error, validation}' job.json
   ```

7. Accept the import only when `state` is `completed` and `validation.passed` is `true`. Point a test copy of your application at the destination before you switch production traffic.

Each response's `region` is the destination's home region. A call that reaches another region answers `421 wrong_region` with an `X-TerraBase-Database-Home` header. Repeat it with an `X-TerraScale-Region-Pin` header set to that region.

## Review the preview

| Field | Meaning |
| --- | --- |
| `tables[]` | One plan per source table: `source`, target `collection`, `rows`, `idPolicy`, `primaryKey`, `columns`, `constraints` and `issues` |
| `columns[].mapping` | How values become JSON; see the [PostgreSQL](/guides/import-postgresql#type-mapping) and [Turso](/guides/import-turso#type-mapping) mappings |
| `issues[]` | `blocking` issues stop the import; warnings are for you to accept. At most 50 per table. |
| `skipped[]` | Objects that are listed and never imported or run, such as functions, triggers, views, sequences and grants. At most 200 entries; `skippedTruncated` marks more. |
| `blocking`, `warnings` | Issue totals. `startImport` is refused while `blocking` is above `0`. |

### Options

| Option | Default | Effect |
| --- | --- | --- |
| `missingPrimaryKey` | `"reject"` | `"reject"` blocks a table without a primary key. `"generate"` gives each row a deterministic ID that stays the same on every resume. |
| `exclude` | `[]` | Leaves out `"schema.table"` or `"schema.table.column"`. A SQLite or Turso schema is `main`. |

## How data maps

- **Collection:** the table name, lowercased. Tables in `public` (PostgreSQL) or `main` (SQLite) drop the schema; others become `schema_table`. Characters outside `a-z`, `0-9`, `_` and `-` become `_`, a name that does not start with a letter gets a `t_` prefix, and names are cut to 63 characters. Two tables that map to one name block the import (`collection_name_collision`).
- **Document ID:** the primary key's canonical text: integer digits, lowercase UUID or the string as is. A composite key joins its parts with `:` after escaping `%` as `%25` and `:` as `%3A`, so key `(1, 2)` becomes `1:2`. IDs are 1 to 256 characters without control characters.
- **Document body:** one JSON member per imported column, named exactly as the column.
- **Constraints are recorded, never enforced.** Foreign keys, unique constraints and `NOT NULL` appear in the preview with `enforced: false`; values are imported unchanged. A foreign key that targets a primary key holds the referenced document's ID. Check constraints, defaults, sequences, indexes, triggers, views, functions, policies, extensions, ownership and grants are skipped.
- **Users, roles and passwords** from the source are never imported. Set up access with [authentication](/guides/authentication).

## Job states

```text
awaiting_source → analyzing → ready → queued → running → validating → completed
                           ↘ rejected          ↘ failed / cancelled
```

| State | What it means | What you can do |
| --- | --- | --- |
| `awaiting_source` | Created, waiting for the file | Upload, or cancel |
| `analyzing` | Building the preview | Wait |
| `ready` | Preview has no blocking issues | Start, change options, or cancel |
| `rejected` | Preview has blocking issues, or the file is unreadable | Change options, or cancel and create a new import for a corrected file |
| `queued`, `running` | Waiting for capacity, then writing documents | Watch `progress`, or cancel |
| `validating` | Comparing counts and checksums | Wait |
| `completed` | Validated | Accept the import |
| `failed`, `cancelled` | Stopped; documents already written stay | Resume while the destination exists |

Repeating a call whose transition already happened answers `202` with the current job, so start, cancel and resume are safe to retry. Any other disallowed transition answers `409 invalid_state`. `Idempotency-Key` is not supported.

## Progress, cancellation and resume

- **Bounded streaming.** The file is read sequentially and written in atomic batches of at most 500 documents and 1.5 MiB. `progress` reports tables and rows done.
- **Cancel** with `api -X POST "$D/import/cancel" | jq .state`. The job stops before its next batch and becomes `cancelled`. Documents already written stay in the destination.
- **Resume** a `failed` or `cancelled` job with `api -X POST "$D/import/resume" | jq .state`. It continues after the last committed batch. Every batch is an upsert under deterministic IDs, so rewriting a batch produces identical documents. If `source.staged` is `false`, upload the same file again first; a different file is refused.
- **Retries.** TerraBase retries storage pressure and capacity refusals with backoff, then fails with `write_failed` and `retryable: true`. A data error fails at once with `retryable: false` and reports its `table`, `row` and `column`.

## Validation

When writing finishes, the job compares, for each table:

- The number of rows in the export with the number of documents in the collection.
- An order-independent checksum of the mapped documents with the same checksum read back from the destination: a SHA-256 of each ID and canonical body, summed.

`validation.tables[]` reports both sides per table. Any mismatch fails the job with `validation_failed`. Validation proves the destination matches the export; it cannot detect writes your source received after you exported. Also spot-check edge values yourself, such as the largest integers, exact decimals, time zones, binary columns, `NULL` and empty strings.

## Errors and recovery

| Response or `error.code` | What to do |
| --- | --- |
| `400 unsupported_source_format` | Re-export in an [accepted format](/guides/import-postgresql#export-the-database). Do not compress the file. |
| `400 source_checksum_mismatch` | The upload differs from the declared size or SHA-256. Upload the exact file you declared. |
| `401`, `403 forbidden_scope` | Use a token with the required scopes. Do not retry automatically. |
| `409 preview_changed`, `409 preview_blocked` | Fetch the job, review the current preview, resolve blocking issues, then start with its `preview.id`. |
| `413 payload_too_large` | The file is over 1 GiB. Export fewer tables into separate imports. |
| `421 wrong_region` | Repeat with `X-TerraScale-Region-Pin`. |
| `503 capacity`, `503 unavailable` | Wait, then retry the same call. |
| `invalid_value`, `invalid_document_id`, `document_too_large` | Fix or exclude the reported column, then import again into a new destination. |
| `write_failed` with `retryable: true` | Resume. |
| `source_lost` | Upload the same file again, then resume. |
| `validation_failed` | Do not use the destination. Compare `validation.tables`, then import again into a new destination. |

A rejected, failed or cancelled import never changes your source.

## Delete a destination

Deleting a database removes all of its documents and its import job. It cannot be undone. Confirm that `DB` names the import destination first: only an import destination answers with a job.

```bash
api "$D/import" | jq '{database, state, sourceSha256: .source.sha256}'
api -X DELETE "$D"
```

## Limits

- 1 GiB per export file, uploaded in one request. A failed upload re-sends the whole file.
- 1000 tables per import. 1 MiB per document; a larger row fails with `document_too_large`.
- Started imports can wait in `queued` while other imports run.
- The first data error stops the job; there is no row-level skip.
- The destination has one home region and no replicas. See [replication](/concepts/replication).

Not supported in contract v1: direct connections to a source database, ongoing sync or change data capture, importing into an existing database, enforcing constraints, resumable partial uploads, and PostgreSQL arrays and composite types.
