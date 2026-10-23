# One-time import from PostgreSQL and Turso (contract v1)

Status, 2026-10-12: **contract v1, revision 3 (clarified), implementation in progress** (branch `feat/postgres-turso-import-core`,
draft PR #167). This file is the single contract for import jobs. Downstream owners (ts-dashboard, ts-dotnet,
ts-docs) build against it; changes are made here first. Revision 2 replaced revision 1 (destination from
`createDatabase`, uploads in parts, no `listImports`). Revision 3 (source integrity): SQLite files must be in rollback
journal mode, SQLite internal/virtual/shadow tables are excluded automatically, a checksum mismatch returns the job to
`awaiting_source`, and the preview maps every row.

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
   and its schema text is only tokenized. A SQLite file in WAL mode is refused, because rows still in its `-wal` would be
   missing without any sign in the file itself.
3. **Dry run first.** Uploading produces a preview. No document is written until the caller confirms that exact
   preview (`startImport` with `previewId`). Every path back into copying (resume included) requires a confirmed
   preview that is still current.
4. **Isolated destination.** The destination is a **new** database the caller provisions for this import with the
   existing `createDatabase` (`POST P/databases`); `createImport` refuses a database that has any collection
   (`409 destination_not_empty`), so a populated database is never adopted. Imported documents are written create-only:
   an import never overwrites a document it did not write; a concurrent ordinary write that collides fails the job
   explicitly (`destination_modified`), and validation fails on any other change to imported collections.
   TerraBase never deletes a destination.
5. **The confirmation binds the artifact and the mapping.** `preview.id` (a ULID) names one analysis of the staged
   bytes with SHA-256 `source.sha256` under the job's `options`; the preview holds the complete plan (tables,
   collections, ids, column mappings). `startImport` must name the current `preview.id`; the copy then applies exactly
   that stored plan and re-checks the SHA-256 before reading. New options or a new analysis mint a new id, and
   `confirmedPreviewId` records what the user confirmed.

## 2. Ownership and routing

- Service: TerraBase (`ts-terrabase`), client listener (TLS), `/v1`, ts-auth **project access tokens** (Bearer JWT,
  `project_id` = the route's project). No new scopes, no ts-auth change. Import routes are project management routes,
  like `createDatabase`: a request without a project token (legacy `tb_` API keys, `X-TerraBase-User-Id`) gets `401`, a
  token of another project, a membership token or a token narrowed to another database gets `404`. Create the destination with the canonical `POST P/databases` and the same project
  access token, so the import routes resolve it; a database created through the hidden organization routes is reachable
  by a project token only once its project binding is verified (api.md, "Authentication").
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

1. `POST P/databases` (existing `createDatabase`, scope `databases:write`) with body
   `{"name":"import-<fresh ULID, lowercase>"}`: a fresh unique name per import, so a `409 name_taken` on a replay can
   only be the caller's own database. Names: a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63.
   `homeRegion` is optional and defaults to the region that receives the request; a request for another home region must
   be sent to that region (`421 wrong_region` elsewhere). Omit `replicaRegions`. The `201` body is the existing
   `ManagedDatabaseBody` (`id`, `projectId`, `name`, `branchedFrom`, `createdAt`, `region`, `homeRegion`,
   `replicaRegions`); every later import call goes to `homeRegion`.
2. `POST D/import` with the source's format, size, SHA-256 and options.
3. `PUT D/import/source?offset=…` part by part (each at most 4 MiB, in order) until `source.receivedBytes` equals
   `source.bytes`; the job moves to `analyzing`.
4. Poll `GET D/import` (every 2 s is enough) until `ready` (preview) or `rejected`.
5. Show the preview; on confirmation `POST D/import/start {"previewId":…}`; poll until `completed` or `failed`.

Plan limits apply unchanged, and an import never bypasses one. The plan's database-count limit belongs to ts-dashboard
(the owner of plans), which checks it before it provisions a destination; TerraBase does not count databases. TerraBase
applies its own limits to imports exactly as to other writes: every imported batch passes the organization's storage
quota (Hobby: 1 GiB of live documents), so exceeding it fails the job with `quota_exceeded` (retryable once the plan
allows more); every import call passes request admission (`429 tenant_busy`/`tenant_memory`); imported documents are
metered as document writes, and import calls, upload bytes included, as requests of the destination.

### Requests

`createImport` body:

```json
{
  "format": "postgres_plain",          // required: "postgres_plain" | "sqlite"
  "sourceBytes": 104857600,            // required: exact source size, 1 … 1073741824 (1 GiB)
  "sourceSha256": "9f86d0…",           // required: 64 lowercase hex characters
  "options": {                         // optional; defaults shown
    "exclude": []                      // at most 1000 of "schema.table" or "schema.table.column"; SQLite schema is "main"
  }
}
```

Replays: a job already attached to the database answers `200` with that job only when the **whole request** is
identical (format, size, SHA-256 and normalized options; the project and database are the route). Anything else
answers `409 import_exists`. A database with collections answers `409 destination_not_empty` (a database holding only
an empty `terrascale-import` collection, left by a create whose first write failed, counts as empty). A database with
replica regions answers `409 destination_replicated`: add replicas after the import completes.

`uploadImportSource`: one part per request, `Content-Type: application/octet-stream`, 1 byte to 4 MiB,
`offset` = bytes already received. A part at `offset` = `source.receivedBytes` is appended. A part that ends at or
before `receivedBytes` is a replay and answers `200` unchanged. Any other offset answers `409 offset_mismatch`, and a
part beyond `sourceBytes` answers `413 payload_too_large`. The part that completes the source moves the job to
`analyzing`, which first checks the SHA-256. Allowed in `awaiting_source`, and in `failed`/`cancelled` when
`source.staged` is false.

**The source of an import never changes.** Every upload must be the file whose size and SHA-256 `createImport`
declared. When the completed bytes hash differently (a transfer fault), TerraBase discards them and the job returns
to `awaiting_source` with `error.code` `source_checksum_mismatch` and `receivedBytes` 0: upload the identical file
again. A different or re-exported file is a new import: create a new database and a new import.

`updateImport` body: `{"options":{…}}`. Allowed in `ready` and `rejected`; re-analyzes, giving a new `preview.id`.

`startImport` body: `{"previewId":"01J…"}`. A stale id answers `409 preview_changed`; blocking issues answer
`409 preview_blocked`.

`cancelImport`, `resumeImport`: empty body.

### `ImportJob`

Every field is always present; `null` where stated, and only there. Counts are JSON integers; times are RFC 3339 UTC
strings. Strings never stand in for null: `mapping` is always a string (an all-NULL SQLite column is the string
`"null"`). `constraints[].name` is null when the source names none, `constraints[].references` is null except for
foreign keys, and `issues[].column`/`issues[].row` are null when the issue has none. Every `ImportJob` response body is
at most 1 MiB (preview at most 512 KiB, validation at most 1000 tables).

```json
{
  "id": "01J9Z…",                       // ULID
  "project": "01J8…",
  "database": "import-01j9z…",          // destination
  "region": "gru",                      // home region; route every import call here
  "state": "ready",                     // section 3
  "cancelRequested": false,
  "options": { "exclude": [] },
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
                                       |--> rejected         (blocking issues or an unreadable/unsupported source)
                                       \--> awaiting_source  (SHA-256 mismatch: bytes discarded, upload the same file again)
ready | rejected --update--> analyzing   (new options only; the source cannot change)
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
| `sqlite` | One consolidated SQLite 3 database file in **rollback journal mode** (header bytes 18–19 = 1), Turso/libSQL databases included | Turso: `turso db export DB --output-file turso-export.db`, then `sqlite3 turso-export.db "VACUUM INTO 'export.db'"` with `turso-export.db-wal` (if written) still beside it. Local SQLite: `sqlite3 local.db "VACUUM INTO 'export.db'"`. Upload `export.db`. Alternative on a **copy** (never the live file): keep `copy.db` and `copy.db-wal` together and run `sqlite3 copy.db "PRAGMA journal_mode=DELETE"`, which folds the WAL in and rewrites the header |

Rejected (`rejected`, `error.code` `unsupported_source_format`): pg_dump custom/directory/tar archives (`PGDMP`),
compressed files, non-UTF-8 dumps, `pg_dumpall` output (`\connect`), files that are not SQLite 3, encrypted SQLite,
and SQLite files in WAL mode (`error.code` `wal_mode_source`). A WAL-mode file may lack rows that are still in its
`-wal`, and no check of the file alone (`quick_check` included) can tell: `turso db export` can write a `.db` and a
`.db-wal`, and the `.db` alone silently misses the newest rows. `sqlite3 … ".backup …"` keeps the WAL flag, so its output
is refused too; `VACUUM INTO` reads the WAL and writes a rollback-mode file. Tested with sqlite3 3.51.2: a WAL-mode
copy whose `-wal` held 4 rows lost all of them without its `-wal`, while `VACUUM INTO` and `journal_mode=DELETE` on the
`.db` + `.db-wal` pair both gave 4 rows and header bytes 1. The rule is conservative: it also refuses a WAL-mode file
that was fully checkpointed (persistent WAL); converting it as above costs one command. It is not a completeness
proof: a rollback-mode file copied while another process wrote to it can still be torn or stale, and TerraBase cannot
detect every such copy (nor a rollback-mode file whose exporter left committed rows in a stray `-wal`); take the
snapshot with `VACUUM INTO` (or pg_dump) as documented. The Turso commands are from Turso's CLI documentation; no
export from a real Turso account has been tested.
Blocking issues: `INSERT` statements (`--inserts`), large objects, `COPY` in CSV/binary form, unknown statements.
pg_dump's `\restrict`/`\unrestrict` lines (PostgreSQL 17.6+) are recognized and ignored.

SQLite tables excluded **automatically** (listed in `skipped`, never imported, never blocking): virtual tables
(`virtual_table`, with a `not_imported` warning: an FTS or R-tree index is rebuilt from its source table, but a virtual
table holding its own content is not imported), the shadow tables SQLite's own modules create for them (`shadow_table`:
fts3/fts4 `_content`, `_segments`, `_segdir`, `_docsize`, `_stat`; fts5 `_data`, `_idx`, `_content`, `_docsize`,
`_config`; rtree/geopoly `_node`, `_parent`, `_rowid`; any other table, `notes_tags` beside `notes` included, is
imported), and SQLite's internal tables `sqlite_sequence`, `sqlite_stat1` … `sqlite_stat4` and any other `sqlite_*`
(`internal_table`). Rows written before `ALTER TABLE … ADD COLUMN` get the column's literal default with the column's
affinity, as SQLite reads them (`INTEGER DEFAULT '5'` is the number 5).

## 5. Type mapping (explicit; anything not listed is `unsupported_type`, blocking until excluded)

PostgreSQL (`COPY` text values; `\N` is JSON `null`):

| Source types | `mapping` | JSON value |
| --- | --- | --- |
| `smallint`, `integer`, `bigint` (and `serial` forms) | `integer` | number, exact digits |
| `real`, `double precision` | `float` | number as dumped; `NaN`, `±Infinity` and numbers TerraBase cannot store (nonzero magnitude below 1e-307, or 1e308 and above) are `invalid_value`; zero (`0`, `-0`) is valid |
| `numeric`, `decimal` | `decimal_string` | string, the exact dumped digits (never rounded); `NaN` stays `"NaN"` |
| `boolean` | `boolean` | `true` / `false` |
| `text`, `varchar`, `char`, `name`, `citext`, enum types declared in the dump | `string` | string (char padding kept) |
| `uuid` | `string` | lowercase canonical string |
| `json`, `jsonb` | `json` | the embedded JSON value (TerraBase limits: 16 levels, 4096 values per document) |
| `bytea` | `base64` | standard base64 string |
| `date` | `date` | `"YYYY-MM-DD"` |
| `timestamp` | `timestamp` | `"YYYY-MM-DDThh:mm:ss[.ffffff]"`, no offset |
| `timestamptz` | `timestamptz` | RFC 3339 UTC `"…Z"`, microseconds kept |
| `time`, `timetz`, `interval`, `inet`, `cidr`, `macaddr`, `macaddr8`, `bit`, `varbit`, `xml` | `text` | string, exactly as dumped |

`date`, `timestamp` and `timestamptz` values `infinity`, `-infinity` and BC values (`… BC`) are `invalid_value`: they
have no RFC 3339 form. Years 1 to 9999 AD are imported.

Unsupported in v1: arrays, composite, range and multirange, domain, geometric, `money`, `tsvector`/`tsquery`,
`pg_lsn`, extension types other than `citext`.

SQLite/Turso: each value maps by its **storage class**, never inferred from the declared type: `INTEGER` → number,
`REAL` → number (`±Infinity`, nonzero magnitudes below 1e-307 and magnitudes of 1e308 and above are `invalid_value`; zero is valid), `TEXT` → string, `BLOB` → base64
string, `NULL` → `null`. `mapping` names the non-NULL classes seen, joined by `+` in the order `integer`, `float`,
`string`, `base64` (for example `integer` or `integer+string`), or `null` when every value is NULL. NULLs never make
a column mixed; two or more non-NULL classes are a `mixed_storage_classes` warning.

**The preview maps every row.** Analysis reads the whole source and applies the mapping, document ids and
TerraBase's document limits to every row, so invalid values, invalid ids and documents over 1 MiB, 16 levels or 4096
values are blocking issues in the preview, with their row numbers (first 50 per table), before anything is written.

## 6. Keys, ids and constraints

- **Names in `exclude`, `source` and `columns[].name`:** dot-separated identifiers. An identifier made only of
  `[a-z0-9_]` and not starting with a digit is written bare; any other is double-quoted with `"` doubled, as in SQL:
  `public."Order Items"."unit.price"`. PostgreSQL names match exactly; SQLite names match case-insensitively.
- **Partitions (PostgreSQL):** each partition holds its own rows and imports as its own collection; the partitioned
  parent holds none and is listed in `skipped` (`partitioned_table`). A dump made with `--load-via-partition-root`
  loads every row into the root, which then imports as one collection.
- **Collection:** the table name (`public` and `main` drop their schema, other schemas give `schema_table`),
  lowercased, characters outside `[a-z0-9_-]` replaced by `_`, prefixed `t_` unless it starts with a letter, cut to
  63. Collisions, reserved names (`collections`, `commit_offset`, `sqlite_*`, `turso_*`) and the import's own
  collection `terrascale-import` are blocking (`collection_name_collision`).
- **Document id:** every row gets a deterministic ULID (time: the job's creation; randomness: SHA-256 of job id, table
  and row number), identical on every resume. Primary key columns, like every other column, stay fields of the body;
  tables need no primary key.
- **Body:** a JSON object with one member per non-excluded column, named exactly as the column.
- **Constraints are recorded, never enforced** (`enforced: false`): primary, unique and foreign keys and NOT NULL.
  Values are imported unchanged, so a foreign key holds the referenced row's primary key field, not its document id. CHECK
  constraints, defaults, sequences, indexes, triggers, views, functions, policies, extensions, ownership and grants are
  listed in `skipped`.
- **The import's record** is kept in the destination's collection `terrascale-import` (documents `0000000000000000000000000J` (job) and `0000000000000000000000000S` (state),
  internal format `{"v":1,"rev":…,"record":"<JSON>"}`; read it through `getImport`, never directly). It stays after
  completion as the import's audit record; dropping that collection forgets the import. The runner re-checks the record
  before copying: a state changed outside the import (for example to `queued` under a blocked preview) fails the job
  with `destination_modified` and copies nothing.

## 7. Consistency, streaming, retries, cancellation, validation

- **Snapshot:** the import reads exactly the uploaded bytes, bound by `sourceSha256` (checked when the upload
  completes and before every run). PostgreSQL consistency is pg_dump's: one transaction snapshot. A SQLite file is
  its own snapshot when it is a consolidated rollback-mode file (`VACUUM INTO`); WAL-mode files are refused (section 4).
- **Writes are create-only and atomic with the checkpoint.** Each batch (at most 500 documents and 1 MiB) is one
  TerraBase transaction holding the documents, each with `if-none-match: *`, and the job's checkpoint (table, row,
  source offset). A crash, restart or resume continues after the last committed batch. A document that already
  exists was not written by the import, so the job fails with `destination_modified` (not retryable) instead of
  overwriting it. Do not write to the destination before it is `completed`.
- **Plan change:** if TerraBase's mapping changed between the confirmed preview and the copy (a deploy in between), the
  job returns to `analyzing` with `error.code` `preview_changed` and no confirmed preview; the user confirms the new one.
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
| Staged sources on one machine | 2 GiB in total and 1 GiB per organization, counting every incomplete upload at its declared size |
| Schema | 1000 tables (refused while reading), 1600 columns per table, 20000 SQLite schema objects or 16 MiB of schema SQL |
| Free volume space | an upload starts only if `2 × sourceBytes + 1 GiB` stays free; otherwise `503 capacity` |
| Concurrent work per machine | one analysis and one copy |
| PostgreSQL line / statement | 16 MiB per line; a statement TerraBase interprets is at most 1 MiB |
| SQLite file | page size 512 to 65536, b-tree depth 32, every page visited at most once |
| Document | TerraBase's: 1 MiB, 16 levels, 4096 values |
| Preview | section 2 |

Staged sources live in the region's TerraBase volume (`<data>/.import/`). They are deleted when the job completes,
when the destination or its import record is deleted, when an upload has been idle for **1 day** (`awaiting_source`;
the job stays and the upload restarts at offset 0), when a preview (`ready`/`rejected`) has been left alone for **7
days**, and **7 days** after the job failed or was cancelled (`source.retainedUntil`, extended by any later upload).
These periods are constants in code (zero-configuration rule): changing them is a code change. This retention must be
disclosed to users before any real upload.

The staged source, the job's lock and its
runner live on the staging machine, and only the machine holding a job's staged source works on it. Pin every import call of a
job to its region (`X-TerraScale-Region-Pin`). If a region ever runs several machines, import calls must also be pinned
to the staging machine before imports are offered there.

Measured locally (Debug build, low CPU priority, a busy 16-core machine): about 400 documents per second through
TerraBase's normal write path, so a source of millions of rows takes hours. Copy throughput on OCI is unmeasured.

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
| 409 | `destination_replicated` | `createImport` on a database with replica regions |
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
`unsupported_type`, `collection_name_collision`, `too_many_tables`,
`too_many_columns`, `preview_too_large`, `malformed_source`, `invalid_value`,
`document_too_large`, `source_lost`, `destination_modified`, `quota_exceeded` (retryable
after the plan allows more), `write_failed`, `validation_failed`, `wal_mode_source`; warnings `mixed_storage_classes`,
`not_imported` (a skipped object that may hold data that is not imported, such as a materialized view or a virtual
table).

## 10. Not supported, and what it would need

- Direct PostgreSQL/Turso connections or `secretRef`: outbound access from TerraBase to customer networks, SSRF
  controls and credential custody. None exists; v1 uses exports.
- Ongoing sync/CDC, imports into databases that hold data, enforcing constraints, PostgreSQL arrays and composite
  types, a row-level skip policy (the first data error fails the job with its location), one collection per
  partitioned table (each partition imports as its own collection).
