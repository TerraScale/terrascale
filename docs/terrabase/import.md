# One-time import from PostgreSQL and Turso (contract v1)

Status, 2026-10-12: **contract v1, revision 2, implementation in progress** (branch `feat/postgres-turso-import-core`,
draft PR #167). This file is the single contract for import jobs. Downstream owners (ts-dashboard, ts-dotnet,
ts-docs) build against it; changes are made here first. Revision 2 replaces revision 1: the destination is
created with `createDatabase`, sources upload in parts, and `listImports` is gone.

Scope: a guided, one-time bulk copy of a **PostgreSQL** or **Turso/libSQL/SQLite** export into a **new, empty,
isolated TerraBase database**. Not in scope: ongoing sync or CDC, direct connections to source databases, imports
into a database that holds data, schema/constraint enforcement in TerraBase.

## 1. Safety model

1. **TerraBase never connects to a source database and never receives source credentials.** The user exports on
   their own machine with their own tools and credentials, then uploads the export. There is no `secretRef` or
   `sourceRef`: direct connections need egress from TerraBase to customer networks and credential custody that do not
   exist (section 10).
2. **Nothing in an export is executed, and nothing that holds data is silently dropped.** A PostgreSQL dump is
   tokenized as text; only table, enum, constraint and `COPY` statements are interpreted. Statements known to hold no
   data (functions, indexes, grants, …) are listed as skipped. Any statement that can hold data and is not imported
   (`INSERT`, large objects, an unknown statement) is a **blocking** issue. A SQLite file is read by TerraBase's own
   bounded, managed, read-only reader of the SQLite file format: no SQLite or Turso engine opens it, no SQL in it runs,
   and its schema text is only tokenized. Virtual tables block until excluded.
3. **Dry run first.** Uploading produces a preview. No document is written until the caller confirms that exact
   preview (`startImport` with `previewId`). Every path back into copying (resume included) requires a confirmed
   preview that is still current.
4. **Isolated destination.** The destination is a database the caller creates with the existing `createDatabase`
   (`POST P/databases`) and that has **no collections** when the import is attached. Imported documents are written
   create-only: an import never overwrites a document it did not write. TerraBase never deletes a destination.

## 2. Ownership and routing

- Service: TerraBase (`ts-terrabase`), client listener (TLS), `/v1`, ts-auth **project access tokens** (Bearer JWT).
  No new scopes, no ts-auth change. Legacy `tb_` API keys get `404`.
- Code: `src/TerraBase/Features/Import/` (jobs, endpoints, PostgreSQL dump reader, SQLite file reader, mapping, runner).
- Bodies: camelCase JSON; errors in the `ErrorBody` envelope `{"error":{"code","message"}}`.
- **Region:** an import runs in its destination's **home region**. Every import route answers `421 wrong_region` with
  `X-TerraBase-Database-Home` anywhere else (as database management routes do). Pin with `X-TerraScale-Region-Pin`.
  Create the destination without replicas (`replicaRegions` omitted); add replicas after the import completes.

`P` = `/v1/projects/{project}`, `D` = `P/databases/{database}`. One destination database holds one import.

| Method | Route | operationId | Scopes (all required) | Success |
| --- | --- | --- | --- | --- |
| POST | `D/import` | `createImport` | `databases:write`, `documents:write` | `201` `ImportJob` (`awaiting_source`); `200` for an identical replay |
| GET | `D/import` | `getImport` | `databases:read` | `200` `ImportJob` |
| PUT | `D/import/source?offset={n}` | `uploadImportSource` | `databases:write`, `documents:write` | `200` `ImportJob` |
| PATCH | `D/import` | `updateImport` | `databases:write`, `documents:write` | `202` `ImportJob` (`analyzing`) |
| POST | `D/import/start` | `startImport` | `databases:write`, `documents:write` | `202` `ImportJob` (`queued`) |
| POST | `D/import/cancel` | `cancelImport` | `databases:write` | `202` `ImportJob` |
| POST | `D/import/resume` | `resumeImport` | `databases:write`, `documents:write` | `202` `ImportJob` |

Listing: `listDatabases` (existing), then `getImport` per database (`404 unknown_import` when it has none).
Cleanup: `deleteDatabase` (existing) removes the destination and its job; TerraBase then deletes the staged source.

### Guided flow

1. `POST P/databases {"name":"import-<fresh ULID, lowercase>"}`: a fresh unique name per import, so a `409 name_taken`
   on a replay can only be the caller's own database.
2. `POST D/import` with the source's format, size, SHA-256 and options.
3. `PUT D/import/source?offset=…` part by part (each at most 4 MiB, in order) until `source.receivedBytes` equals
   `source.bytes`; the job moves to `analyzing`.
4. Poll `GET D/import` (every 2 s is enough) until `ready` (preview) or `rejected`.
5. Show the preview; on confirmation `POST D/import/start {"previewId":…}`; poll until `completed` or `failed`.

### Requests

`createImport` body:

```json
{
  "format": "postgres_plain",          // required: "postgres_plain" | "sqlite"
  "sourceBytes": 104857600,            // required: exact source size, 1 … 1073741824 (1 GiB)
  "sourceSha256": "9f86d0…",           // required: 64 lowercase hex characters
  "options": {                         // optional; defaults shown
    "missingPrimaryKey": "reject",     // "reject" | "generate"
    "exclude": []                      // at most 1000 of "schema.table" or "schema.table.column"; SQLite schema is "main"
  }
}
```

Replays: a job already attached to the database answers `200` with that job only when the **whole request** is
identical (format, size, SHA-256 and normalized options; the project and database are the route). Anything else
answers `409 import_exists`. A database with collections answers `409 destination_not_empty`.

`uploadImportSource`: one part per request, `Content-Type: application/octet-stream`, 1 byte to 4 MiB,
`offset` = bytes already received. A part at `offset` = `source.receivedBytes` is appended. A part that ends at or
before `receivedBytes` is a replay and answers `200` unchanged. Any other offset answers `409 offset_mismatch`, and a
part beyond `sourceBytes` answers `413 payload_too_large`. The part that completes the source moves the job to
`analyzing`, which first checks the SHA-256. Allowed in `awaiting_source`, and in `failed`/`cancelled` when
`source.staged` is false; the bytes must hash to the declared `sourceSha256`.

`updateImport` body: `{"options":{…}}`. Allowed in `ready` and `rejected`; re-analyzes, giving a new `preview.id`.

`startImport` body: `{"previewId":"01J…"}`. A stale id answers `409 preview_changed`; blocking issues answer
`409 preview_blocked`.

`cancelImport`, `resumeImport`: empty body.

### `ImportJob`

Every field is always present; `null` where stated. Counts are JSON integers; times are RFC 3339 UTC strings.

```json
{
  "id": "01J9Z…",                       // ULID
  "project": "01J8…",
  "database": "import-01j9z…",          // destination
  "region": "gru",                      // home region; route every import call here
  "state": "ready",                     // section 3
  "cancelRequested": false,
  "options": { "missingPrimaryKey": "reject", "exclude": [] },
  "source": {
    "format": "postgres_plain",
    "bytes": 104857600,
    "sha256": "9f86d0…",
    "receivedBytes": 104857600,
    "staged": true,                     // TerraBase holds the complete, verified source now
    "retainedUntil": null               // set once the job is failed or cancelled
  },
  "preview": null | {
    "id": "01J9Z…",                     // confirm with startImport
    "tables": [ TablePlan ],
    "skipped": [ { "kind": "function", "name": "public.audit()" } ],
    "skippedTruncated": false,          // at most 200 entries
    "blocking": 0,                      // blocking issues; start is refused while > 0
    "warnings": 2
  },
  "confirmedPreviewId": null,           // the preview startImport confirmed; resume copies only under it
  "progress": null | {
    "tablesDone": 3, "tablesTotal": 12,
    "rowsWritten": 120000, "rowsTotal": 480000,
    "currentTable": "public.orders"     // null when not copying
  },
  "validation": null | {
    "passed": true,
    "tables": [ { "source": "public.users", "collection": "users",
                  "sourceRows": 1000, "documents": 1000,
                  "sourceChecksum": "hex", "documentChecksum": "hex", "match": true } ]
  },
  "error": null | {
    "code": "invalid_value", "message": "…", "retryable": false,
    "table": "public.orders", "row": 1834, "column": "total"   // each null when not applicable
  },
  "createdAt": "…", "updatedAt": "…", "startedAt": null, "finishedAt": null
}
```

`TablePlan`:

```json
{
  "source": "public.users",            // schema.table ("main.users" for SQLite)
  "collection": "users",
  "excluded": false,
  "rows": 1000,                         // rows in the export
  "idPolicy": "primary_key",            // "primary_key" | "rowid" | "generated" | "none"
  "primaryKey": ["id"],
  "columns": [ { "name": "id", "sourceType": "bigint", "mapping": "integer",
                 "nullable": false, "excluded": false } ],
  "constraints": [ { "kind": "foreign_key", "name": "users_org_fk", "columns": ["org_id"],
                     "references": "public.orgs(id)", "enforced": false } ],
  "issues": [ { "severity": "blocking", "code": "unsupported_type", "column": "tags", "row": null,
                "message": "integer[] has no mapping; exclude the column" } ]
}
```

Bounds: 1000 tables, 1600 columns per table, 50 issues per table, 200 skipped entries; a preview whose JSON exceeds
512 KiB is `rejected` with `preview_too_large` (exclude tables or import in parts).

## 3. States

```text
awaiting_source --last part--> analyzing --> ready --start(previewId)--> queued --> running --> validating --> completed
                                       \--> rejected   (blocking issues, unreadable source or SHA-256 mismatch)
ready | rejected --update--> analyzing
cancel: awaiting_source | analyzing | ready | rejected | queued | running | validating --> cancelled
failure: analyzing | running | validating --> failed  (error.retryable)
resume from failed/cancelled:
  confirmedPreviewId set (copying had been confirmed) --> queued, under that same preview
  confirmedPreviewId null (never confirmed)           --> analyzing (fresh preview; start must confirm it)
  either, while source.staged is false                --> 409 source_required: upload the same bytes first
```

- `completed` is terminal. `failed`/`cancelled` are resumable while the destination exists.
- Not shown above: `409 invalid_state`. Repeating a call whose effect already holds (start while `queued`/`running`
  with the same `previewId`, cancel while `cancelled`, resume while `queued`/`running`/`analyzing`) answers `202` with
  the current job.
- Cancellation sets `cancelRequested`; a running job stops before its next batch (seconds). Written documents stay.
- One machine copies one import at a time; others wait in `queued`. Analysis runs one at a time too.
- `Idempotency-Key` stays unsupported; replay rules are in section 2.

## 4. Source formats

| `format` | Accepted input | How to produce it |
| --- | --- | --- |
| `postgres_plain` | `pg_dump` plain-text SQL in UTF-8 with `COPY … FROM stdin` data (pg_dump's default) | `pg_dump --format=plain --no-owner --no-privileges --encoding=UTF8 -f dump.sql <db>`; convert a custom-format archive offline with `pg_restore -f dump.sql archive.dump` |
| `sqlite` | One SQLite 3 database file (Turso/libSQL databases in the SQLite file format), checkpointed (no `-wal`) | `sqlite3 local.db ".backup export.db"` or `VACUUM INTO 'export.db'`; for Turso, a database export in the SQLite file format (exact Turso CLI command: ts-docs to verify) |

Rejected (`rejected`, `error.code` `unsupported_source_format`): pg_dump custom/directory/tar archives (`PGDMP`),
compressed files, non-UTF-8 dumps, `pg_dumpall` output (`\connect`), files that are not SQLite 3, encrypted SQLite.
Blocking issues: `INSERT` statements (`--inserts`), large objects, `COPY` in CSV/binary form, unknown statements.

## 5. Type mapping (explicit; anything not listed is `unsupported_type`, blocking until excluded)

PostgreSQL (`COPY` text values; `\N` is JSON `null`):

| Source types | `mapping` | JSON value |
| --- | --- | --- |
| `smallint`, `integer`, `bigint` (and `serial` forms) | `integer` | number, exact digits |
| `real`, `double precision` | `float` | number as dumped; `NaN`, `±Infinity` and values TerraBase cannot store exactly (below 1e-307 in magnitude) are `invalid_value` |
| `numeric`, `decimal` | `decimal_string` | string, the exact dumped digits (never rounded); `NaN` stays `"NaN"` |
| `boolean` | `boolean` | `true` / `false` |
| `text`, `varchar`, `char`, `name`, `citext`, enum types declared in the dump | `string` | string (char padding kept) |
| `uuid` | `string` | lowercase canonical string |
| `json`, `jsonb` | `json` | the embedded JSON value (TerraBase limits: 16 levels, 4096 values per document) |
| `bytea` | `base64` | standard base64 string |
| `date` | `date` | `"YYYY-MM-DD"`; BC dates and `±infinity` are `invalid_value` |
| `timestamp` | `timestamp` | `"YYYY-MM-DDThh:mm:ss[.ffffff]"`, no offset |
| `timestamptz` | `timestamptz` | RFC 3339 UTC `"…Z"`, microseconds kept |
| `time`, `timetz`, `interval`, `inet`, `cidr`, `macaddr`, `macaddr8`, `bit`, `varbit`, `xml` | `text` | string, exactly as dumped |

Unsupported in v1: arrays, composite, range and multirange, domain, geometric, `money`, `tsvector`/`tsquery`,
`pg_lsn`, extension types other than `citext`.

SQLite/Turso: each value maps by its **storage class**, never inferred from the declared type: `INTEGER` → number,
`REAL` → number (`±Infinity` and magnitudes below 1e-307 are `invalid_value`), `TEXT` → string, `BLOB` → base64
string, `NULL` → `null`. `mapping` reports the classes seen (`integer`, `float`, `string`, `base64`, `null`), and a
column holding several is a `mixed_storage_classes` warning.

## 6. Keys, ids and constraints

- **Collection:** the table name (`public` and `main` drop their schema, other schemas give `schema_table`),
  lowercased, characters outside `[a-z0-9_-]` replaced by `_`, prefixed `t_` unless it starts with a letter, cut to
  63. Collisions, reserved names (`collections`, `commit_offset`, `sqlite_*`, `turso_*`) and the import's own
  collection `terrascale-import` are blocking (`collection_name_collision`).
- **Document id:** from the primary key. One column: its canonical text (integer digits, uuid lowercase, string as
  is). Several: each component with `%` → `%25` and `:` → `%3A`, joined by `:`. SQLite rowid tables without a
  primary key use the rowid (`idPolicy` `rowid`). Ids must be 1 to 256 characters without control characters
  (`invalid_document_id`). Primary keys over `float`, `json`, `base64` or nullable columns are blocking
  (`unsupported_primary_key`).
- **No primary key** (PostgreSQL): `missingPrimaryKey: "reject"` (default) blocks the table (`missing_primary_key`);
  `"generate"` gives each row a deterministic ULID (time: the job's creation; randomness: SHA-256 of job id, table and
  row number), identical on every resume.
- **Body:** a JSON object with one member per non-excluded column, named exactly as the column.
- **Constraints are recorded, never enforced** (`enforced: false`): primary, unique and foreign keys and NOT NULL.
  Values are imported unchanged, so a foreign key to a primary key resolves to the referenced document's id. CHECK
  constraints, defaults, sequences, indexes, triggers, views, functions, policies, extensions, ownership and grants are
  listed in `skipped`.
- **The import's record** is kept in the destination's collection `terrascale-import` (documents `job` and `state`).
  It stays after completion as the import's audit record; dropping that collection forgets the import.

## 7. Consistency, streaming, retries, cancellation, validation

- **Snapshot:** the import reads exactly the uploaded bytes, bound by `sourceSha256` (checked when the upload
  completes and before every run). PostgreSQL consistency is pg_dump's: one transaction snapshot. A SQLite file is
  its own snapshot when taken with `.backup` or `VACUUM INTO`; a file in WAL mode is a warning
  (`wal_mode_source`: data still in its `-wal` is not in the file).
- **Writes are create-only and atomic with the checkpoint.** Each batch (at most 500 documents and 1 MiB) is one
  TerraBase transaction holding the documents, each with `if-none-match: *`, and the job's checkpoint (table, row,
  source offset). A crash, restart or resume continues after the last committed batch. A document that already
  exists was not written by the import, so the job fails with `destination_modified` (not retryable) instead of
  overwriting it. Do not write to the destination before it is `completed`.
- **Retries:** storage pressure and capacity refusals are retried with backoff (1 s doubling to 30 s, 8 attempts),
  then `failed` with `write_failed`, `retryable: true`. Data errors fail at once with `retryable: false`.
- **Validation** (`validating`): per table, the source row count against the documents in the collection, and an
  order-independent checksum (the 256-bit sum of SHA-256 over each id and canonical body) of the mapped documents
  against the same checksum read back. Any mismatch fails with `validation_failed`.
- **Source databases are never touched. Destinations are never deleted automatically.**

## 8. Bounds

| Bound | Value |
| --- | --- |
| Source | 1 GiB per import |
| Upload part | 4 MiB (the client listener's body limit) |
| Staged sources on one machine | 2 GiB in total, counting every incomplete upload at its declared size |
| Free volume space | an upload starts only if `2 × sourceBytes + 1 GiB` stays free; otherwise `503 capacity` |
| Concurrent work per machine | one analysis and one copy |
| PostgreSQL line / statement | 16 MiB per line; a statement TerraBase interprets is at most 1 MiB |
| SQLite file | page size 512 to 65536, b-tree depth 32, every page visited at most once |
| Document | TerraBase's: 1 MiB, 16 levels, 4096 values |
| Preview | section 2 |

Staged sources live in the region's TerraBase volume (`<data>/.import/`). They are deleted when the job completes,
when the destination is deleted, when a new upload replaces them, and **7 days** after the job failed or was
cancelled (`source.retainedUntil`). The 7 days are a constant in code (zero-configuration rule): changing it is a code
change. This retention must be disclosed to users before any real upload.

## 9. Error codes

HTTP (`ErrorBody`):

| Status | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | malformed body, option or offset |
| 403 | `forbidden_scope` | token lacks a required scope |
| 404 | `unknown_database` | no such destination (or another project's) |
| 404 | `unknown_import` | the database has no import |
| 409 | `destination_not_empty` | `createImport` on a database with collections |
| 409 | `import_exists` | `createImport` differs from the attached import |
| 409 | `invalid_state` | transition not allowed from the current state |
| 409 | `offset_mismatch` | upload part not at `receivedBytes` |
| 409 | `source_required` | resume needs the source uploaded again |
| 409 | `preview_changed` | `previewId` is not the current preview |
| 409 | `preview_blocked` | the preview has blocking issues |
| 413 | `payload_too_large` | `sourceBytes` over 1 GiB, a part over 4 MiB or past `sourceBytes` |
| 421 | `wrong_region` | not the destination's home region |
| 503 | `capacity` | staging budget or volume space exhausted, or the work queue is full; retry later |
| 503 | `unavailable` | storage unavailable |

Job `error.code` and issue codes: `unsupported_source_format`, `source_checksum_mismatch`, `unsupported_statement`,
`unsupported_type`, `unsupported_primary_key`, `missing_primary_key`, `collection_name_collision`, `too_many_tables`,
`too_many_columns`, `preview_too_large`, `malformed_source`, `invalid_value`, `invalid_document_id`,
`document_too_large`, `source_lost`, `destination_modified`, `write_failed`, `validation_failed`; warnings
`mixed_storage_classes`, `wal_mode_source`, `not_imported` (a skipped object that may hold derived data, such as a
materialized view).

## 10. Not supported, and what it would need

- Direct PostgreSQL/Turso connections or `secretRef`: outbound access from TerraBase to customer networks, SSRF
  controls and credential custody. None exists; v1 uses exports.
- Ongoing sync/CDC, imports into databases that hold data, enforcing constraints, PostgreSQL arrays and composite
  types, a row-level skip policy (the first data error fails the job with its location), one collection per
  partitioned table (each partition imports as its own collection).
