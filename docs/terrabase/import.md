# One-time import from PostgreSQL and Turso (contract v1)

Status, 2026-10-12: **contract proposal, implementation in progress** (branch `feat/postgres-turso-import-core`).
This file is the single contract for import jobs. Downstream owners (ts-dashboard, ts-dotnet, ts-docs) build
against it; changes to it are made here first.

Scope: a guided, one-time bulk copy of a **PostgreSQL** or **Turso/libSQL/SQLite** export into a **new, isolated
TerraBase database**. Not in scope: ongoing sync or CDC, direct connections to source databases, imports into an
existing database, schema/constraint enforcement in TerraBase.

## 1. Safety model

1. **TerraBase never connects to a source database and never receives source credentials.** The user exports on
   their own machine with their own tools and credentials, then uploads the export file. There is no `secretRef`
   or `sourceRef` in v1: direct connections would need egress from TerraBase to customer networks and credential
   custody that does not exist (see section 10).
2. **Nothing in an export is executed.** A PostgreSQL dump is parsed as text: only `CREATE TABLE`, `CREATE TYPE … AS
   ENUM`, `ALTER TABLE … ADD CONSTRAINT` (primary key, unique, foreign key) and `COPY … FROM stdin` blocks are read;
   every other statement (functions, triggers, extensions, `DO`, grants, `\` meta-commands, …) is listed in the
   preview as skipped and never run. A SQLite file is opened read-only by TerraBase's own SQLite with
   `trusted_schema=OFF`, checked with `PRAGMA quick_check`, and read only through `SELECT` on ordinary tables;
   views, triggers and virtual tables are listed as skipped and never queried.
3. **Dry run first.** Uploading a source produces a preview (tables, type mapping, issues, row counts). No document
   is written until the caller confirms that exact preview with `startImport`.
4. **Isolated destination.** Every import creates its own new database (home region only, no replicas). An import
   never writes into an existing database. TerraBase never deletes the destination on its own.

## 2. Ownership and routing

- Service: TerraBase (`ts-terrabase`), client listener (TLS), `/v1`, ts-auth **project access tokens** (Bearer JWT).
  No new scopes and no ts-auth change.
- Code: `src/TerraBase/Features/Import/` (jobs, parsers, mapping, runner),
  `src/TerraBase/Engine/Sqlite/` (the read-only SQLite source reader: SQLite types stay inside the engine).
- Bodies: camelCase JSON, management conventions (`limit`/`after` → `next`), errors in the `ErrorBody` envelope
  `{"error":{"code","message"}}`.
- **Region:** an import lives in the region that received `createImport` (its destination's home region). Every
  other import route must reach that region: elsewhere it answers `421 wrong_region` with
  `X-TerraBase-Database-Home`, as for databases. Pin with `X-TerraScale-Region-Pin` (region identity).

`P` = `/v1/projects/{project}`, `D` = `P/databases/{database}` where `{database}` is the import's destination.
One destination database holds exactly one import, so job routes are singular under `D`.

| Method | Route | operationId | Scopes (all required) | Success |
| --- | --- | --- | --- | --- |
| POST | `P/imports` | `createImport` | `databases:write`, `documents:write` | `201` `ImportJob`, `Location: D/import` |
| GET | `P/imports` | `listImports` | `databases:read` | `200` `{"items":[ImportJob],"next":string\|null}` |
| GET | `D/import` | `getImport` | `databases:read` | `200` `ImportJob` |
| PUT | `D/import/source` | `uploadImportSource` | `databases:write`, `documents:write` | `202` `ImportJob` (`analyzing`) |
| PATCH | `D/import` | `updateImport` | `databases:write`, `documents:write` | `202` `ImportJob` (`analyzing`) |
| POST | `D/import/start` | `startImport` | `databases:write`, `documents:write` | `202` `ImportJob` (`queued`/`running`) |
| POST | `D/import/cancel` | `cancelImport` | `databases:write` | `202` `ImportJob` |
| POST | `D/import/resume` | `resumeImport` | `databases:write`, `documents:write` | `202` `ImportJob` (`queued`) |

Cleanup of the destination is the existing `DELETE D` (`deleteDatabase`); it removes the job with it.

### Requests

`createImport` body:

```json
{
  "format": "postgres_plain",          // required: "postgres_plain" | "sqlite"
  "sourceBytes": 104857600,            // required: exact upload size, 1 … 1073741824 (1 GiB)
  "sourceSha256": "9f86d0…",           // required: lowercase hex SHA-256 of the upload
  "database": "shop-import",           // optional: new destination name; default "import-<id lowercased>"
  "options": {                         // optional; defaults shown
    "missingPrimaryKey": "reject",     // "reject" | "generate"
    "exclude": []                      // "schema.table" or "schema.table.column"; SQLite schema is "main"
  }
}
```

`uploadImportSource`: raw bytes, `Content-Type: application/octet-stream`, `Content-Length` equal to `sourceBytes`.
Allowed in `awaiting_source` and `rejected` (replaces the staged source), and in `failed`/`cancelled` when
`source.staged` is false (the bytes must have the original `sourceSha256`). Re-sending the same bytes is safe.

`updateImport` body: `{"options":{…}}` (same shape as above). Allowed in `ready` and `rejected`; re-analyzes the
staged source and produces a new `preview.id`.

`startImport` body: `{"previewId":"01J…"}`: the `preview.id` the user confirmed. Mismatch: `409 preview_changed`.

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
    "staged": true,                     // the uploaded bytes are held by TerraBase now
    "retainedUntil": "2026-10-19T12:00:00Z"   // null while awaiting or running
  },
  "preview": null | {                   // null until analysis finishes
    "id": "01J9Z…",                     // pass to startImport
    "tables": [ TablePlan ],
    "skipped": [ { "kind": "function", "name": "public.audit()", "reason": "not_imported" } ],
    "skippedTruncated": false,          // at most 200 entries
    "blocking": 0,                      // issues with severity "blocking": start is refused while > 0
    "warnings": 2
  },
  "progress": null | {                  // null before start
    "tablesDone": 3, "tablesTotal": 12,
    "rowsWritten": 120000, "rowsTotal": 480000,
    "currentTable": "public.orders"     // null when not writing
  },
  "validation": null | {                // set when state is completed or validation failed
    "passed": true,
    "tables": [ { "source": "public.users", "collection": "users",
                  "sourceRows": 1000, "documents": 1000,
                  "sourceChecksum": "hex", "documentChecksum": "hex", "match": true } ]
  },
  "error": null | {                     // the failure that ended or blocks the job
    "code": "invalid_value", "message": "…", "retryable": false,
    "table": "public.orders", "row": 1834, "column": "total"    // each null when not applicable
  },
  "createdAt": "…", "updatedAt": "…", "startedAt": null, "finishedAt": null
}
```

`TablePlan`:

```json
{
  "source": "public.users",            // schema.table ("main.users" for SQLite)
  "collection": "users",
  "rows": 1000,                         // rows in the export
  "idPolicy": "primary_key",            // "primary_key" | "generated" | "none" (blocked)
  "primaryKey": ["id"],
  "columns": [ { "name": "id", "sourceType": "bigint", "mapping": "integer",
                 "nullable": false, "excluded": false } ],
  "constraints": [ { "kind": "foreign_key", "name": "users_org_fk", "columns": ["org_id"],
                     "references": "public.orgs(id)", "enforced": false } ],
  "issues": [ { "severity": "blocking", "code": "unsupported_type", "column": "tags",
                "message": "integer[] has no mapping; exclude the column" } ]
}
```

At most 1000 tables per import; `issues` holds at most 50 entries per table.

## 3. States

```text
awaiting_source --upload--> analyzing --> ready ----start----> queued --> running --> validating --> completed
                                      \-> rejected  (blocking issues or unreadable source; upload again or update)
ready/rejected --update--> analyzing
running/queued --cancel--> cancelled (via cancelRequested; at most one batch later)
analyzing/running/validating --error--> failed (error.retryable says whether resume can succeed)
failed/cancelled --resume--> queued   (re-upload first when source.staged is false)
awaiting_source/ready/rejected --cancel--> cancelled
```

Terminal: `completed`. `failed` and `cancelled` are resumable while the destination exists. Transitions not shown
answer `409 invalid_state`. Repeating a call whose transition already happened (start while `queued`/`running`,
cancel while `cancelled`, resume while `queued`/`running`) answers `202` with the current job: safe to retry.
`Idempotency-Key` stays unsupported: `createImport` replays are made safe by sending an explicit `database` name;
a replay then answers `409 name_taken`, and `getImport` on that name shows whether it is the caller's job
(`source.sha256`).

One machine runs one import at a time; other started imports wait in `queued`.

## 4. Source formats

| `format` | Accepted input | How to produce it |
| --- | --- | --- |
| `postgres_plain` | `pg_dump` plain-text SQL (UTF-8) with `COPY … FROM stdin` data, the default `pg_dump` output | `pg_dump --format=plain --no-owner --no-privileges --encoding=UTF8 -f dump.sql <db>`; a custom-format archive converts offline with `pg_restore -f dump.sql archive.dump` |
| `sqlite` | One SQLite 3 database file (Turso/libSQL databases in the SQLite file format), without its `-wal` | a consistent single-file copy, for example `sqlite3 local.db ".backup export.db"` or `VACUUM INTO 'export.db'`; for Turso, a database export/dump file (exact Turso CLI command: ts-docs to verify) |

Refused with `400 unsupported_source_format` at upload: pg_dump custom/directory/tar archives (`PGDMP`), compressed
files, `INSERT`-style dumps (`--inserts`, `--column-inserts`), non-UTF-8 dumps, encrypted or non-SQLite files, a
SQLite file that fails `quick_check`, SQLite schemas SQLite cannot parse (for example libSQL-only syntax).

## 5. Type mapping (explicit; anything not listed is `unsupported_type`, blocking)

PostgreSQL (`COPY` text values; `\N` is JSON `null` for every type):

| Source types | `mapping` | JSON value |
| --- | --- | --- |
| `smallint`, `integer`, `bigint` (incl. `serial` forms) | `integer` | number, exact digits (64-bit) |
| `real`, `double precision` | `float` | number as dumped (shortest round-trip); `NaN`/`±Infinity` fail with `invalid_value` |
| `numeric`, `decimal` | `decimal_string` | string, exact text (never rounded); `NaN` stays `"NaN"` |
| `boolean` | `boolean` | `true`/`false` |
| `text`, `varchar`, `char`, `name`, `citext`, enum types declared in the dump | `string` | string (char padding kept) |
| `uuid` | `string` | lowercase canonical string |
| `json`, `jsonb` | `json` | the embedded JSON value |
| `bytea` | `base64` | standard base64 string |
| `date` | `date` | `"YYYY-MM-DD"`; BC dates and `±infinity` fail with `invalid_value` |
| `timestamp` | `timestamp` | `"YYYY-MM-DDThh:mm:ss[.ffffff]"` (no offset) |
| `timestamptz` | `timestamptz` | RFC 3339 UTC `"…Z"`, microseconds kept |
| `time`, `timetz`, `interval`, `inet`, `cidr`, `macaddr`, `bit`, `varbit`, `xml` | `text` | string, exact dumped text |

Unsupported in v1 (exclude the column or table): arrays, composite, range and multirange, domain, geometric,
`money`, `tsvector`/`tsquery`, `pg_lsn`, extension types.

SQLite/Turso: each value maps by its **storage class**, never by inference from the declared type: `INTEGER` →
number, `REAL` → number (`±Infinity` fails with `invalid_value`), `TEXT` → string, `BLOB` → base64 string, `NULL`
→ `null`. `mapping` reports the classes seen (`integer`, `float`, `string`, `base64`, or `mixed` as a warning).

## 6. Keys, ids and constraints

- **Collection:** the table name (`public` and `main` drop their schema prefix, other schemas become
  `schema_table`), lowercased, characters outside `[a-z0-9_-]` replaced by `_`, prefixed `t_` when it does not
  start with a letter, cut to 63. Two tables mapping to one name, or a reserved name, are blocking
  (`collection_name_collision`).
- **Document id:** from the primary key (PostgreSQL primary key constraint; SQLite `PRIMARY KEY`, or `rowid` for
  rowid tables without one). One column: its canonical text (integer digits, uuid lowercase, string as is).
  Several columns: each component with `%` → `%25` and `:` → `%3A`, joined by `:`. An id must be 1 to 256
  characters without control characters, or the row fails with `invalid_document_id`. Primary keys over `float`,
  `json`, `base64` or nullable columns are blocking (`unsupported_primary_key`).
- **No primary key:** `missingPrimaryKey: "reject"` (default) blocks the table (`missing_primary_key`);
  `"generate"` gives each row a deterministic ULID (timestamp: the job's creation; randomness: SHA-256 of job id,
  table and row ordinal), identical on every resume.
- **Body:** a JSON object with one member per non-excluded column, named exactly as the column.
- **Constraints are recorded, never enforced** (`enforced: false`): foreign keys, unique constraints and NOT NULL
  appear in the preview; values are imported unchanged, so a foreign key that targets a primary key resolves to the
  referenced document's id. CHECK constraints, defaults, sequences, indexes, triggers, views, functions, policies,
  extensions, ownership and grants are listed in `skipped`.

## 7. Consistency, streaming, retries, cancellation, validation

- **Snapshot:** the import reads exactly the uploaded file, bound by `sourceSha256` (verified at upload and before
  every run). PostgreSQL consistency is pg_dump's own: it dumps from one transaction snapshot. A SQLite file is its
  own snapshot; take it with `.backup` or `VACUUM INTO`, never by copying a live file.
- **Staging:** the upload is stored on the region's TerraBase volume until the job completes, is re-uploaded, or 7
  days after it failed or was cancelled (`source.retainedUntil`); a lost staged source sets `source.staged` false.
- **Bounded streaming:** the source is read sequentially with fixed buffers; documents are written in atomic batches
  of at most 500 documents and 1.5 MiB; a document over 1 MiB fails with `document_too_large`.
- **Idempotent and resumable:** every batch is an upsert under deterministic ids, committed atomically with the
  job's checkpoint (table, row ordinal, source offset). A crash, restart or `resumeImport` continues after the last
  committed batch; re-writing a batch writes identical documents.
- **Retries:** storage pressure and capacity refusals are retried with backoff (1 s doubling to 30 s, 8 attempts),
  then `failed` with `write_failed`, `retryable: true`. Data errors fail at once with `retryable: false`.
- **Cancellation:** sets `cancelRequested`; the runner stops before its next batch and moves to `cancelled`.
  Documents already written stay in the destination; delete the database to discard them.
- **Validation** (`validating`): per table, the source row count against the documents in the collection, and an
  order-independent checksum of the mapped documents (SHA-256 per id and canonical body, summed) against the same
  checksum read back from the destination. Any mismatch fails the job with `validation_failed`.
- **Source databases are never touched.** Destinations are never deleted automatically.

## 8. Error codes

HTTP (`ErrorBody`):

| Status | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | malformed body or option |
| 400 | `unsupported_source_format` | upload is not an accepted format (section 4) |
| 400 | `source_checksum_mismatch` | upload size or SHA-256 differs from the declared values |
| 403 | `forbidden_scope` | token lacks a required scope |
| 404 | `unknown_database` | no such destination (or another project's) |
| 404 | `unknown_import` | the database has no import |
| 409 | `name_taken` | the destination name exists |
| 409 | `invalid_state` | transition not allowed from the current state |
| 409 | `preview_changed` | `previewId` is not the current preview |
| 409 | `preview_blocked` | the preview has blocking issues |
| 413 | `payload_too_large` | `sourceBytes` over 1 GiB, or the upload exceeds `sourceBytes` |
| 421 | `wrong_region` | not the import's region |
| 503 | `capacity` / `unavailable` | not enough volume space for the upload, or storage unavailable |

Job `error.code` and preview issue codes: `unsupported_type`, `unsupported_primary_key`, `missing_primary_key`,
`collection_name_collision`, `too_many_tables`, `malformed_source`, `invalid_value`, `invalid_document_id`,
`document_too_large`, `source_lost`, `write_failed`, `validation_failed`, `mixed_storage_classes` (warning).

## 9. Limits (v1)

1 GiB per source; 1000 tables; 1 MiB per document; one running import per machine; uploads in one request (retry
re-sends the whole file); no row-level skip policy (first data error fails the job with its location).

## 10. Not supported, and what it would need

- Direct PostgreSQL/Turso connections or `secretRef`: needs outbound access from TerraBase to customer networks,
  SSRF controls and credential custody (ts-auth or Fly secrets per customer). None exists; v1 uses exports.
- Ongoing sync/CDC, imports into existing databases, enforcing constraints, resumable (ranged) uploads,
  PostgreSQL arrays and composite types.
