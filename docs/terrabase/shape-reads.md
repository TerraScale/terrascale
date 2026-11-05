# Shape reads, protocol v2 (SQL tables)

A shape is the rows of one SQL table that match an optional `where`, with an optional column list. Clients sync it over plain HTTP GET as a sealed snapshot, then
sealed change chunks, then a live tail, Electric style (PIVOT D8). Every response is addressed by `(handle, offset)`,
so identical URLs return identical bytes and any cache can serve many readers for the cost of one.

End users read shapes from the browser under table read grants: [shape read grants](shape-read-grants.md). The local
overlay (F7) and region-to-region provisional changes (F8) are later tasks.

## 1. Source: the home database's change capture

- Every TerraBase write connection runs native change capture in `full` mode
  (`PRAGMA capture_data_changes_conn('full')`). Each committed transaction appends its row changes, with before and
  after images, to the database file's native capture table in the same transaction, then one commit record
  (`change_type = 2`). Before images support F2's move-in and move-out. `INSERT … ON CONFLICT DO UPDATE` needs native
  patch 0008 (the backport of upstream `26dbb464`), which the pinned native release carries.
- Shapes read only the native capture table and the table itself.
- Only the database's home serves and seals shapes. Region replicas are not served yet (`replicas_unavailable`), so
  every sealed chunk holds only home-committed, home-confirmed changes. When region writes arrive (PIVOT R4 to R7),
  they reach shapes through the home apply, which commits through the same capture.
- The native capture table uses `AUTOINCREMENT` change ids, so ids are never reused, also after retention trims old rows.

## 2. Route

`GET /v1/projects/{project}/databases/{database}/tables/{table}/shape?handle=&offset=&live=&cursor=&where=&columns=`

Client listener, TLS, `sql:read`, OpenAPI tag `Live`. End-user tokens read only granted tables: [shape read grants](shape-read-grants.md).

| Parameter | Value |
| --- | --- |
| `offset` | Required: `-1`, `snap.<S>.<P>`, or a decimal commit boundary `O`. |
| `handle` | Required unless `offset=-1`. A bootstrap with a handle is `409 must_refetch`. |
| `live` | `true`, only with a decimal `O`. |
| `cursor` | Only with `live=true`: the decimal live bucket. |
| `where` | Optional SQL condition on the table's columns, at most 4096 characters (section 2a). Every row when absent. |
| `columns` | Optional comma-separated column names, at most 4096 characters. Every stored column when absent. |

Send the same `where` and `columns` on every request of a shape, including `/keys` and `/manifest`; the `307` from
`offset=-1` keeps them in `Location`.

Validation answers `400 invalid_request`: unknown or repeated parameters, non-canonical decimals (no sign or leading
zeros, at most `9223372036854775807`), a malformed handle, `live` without a decimal offset, `cursor` without `live`.

`table` names an ordinary rowid table: 1 to 63 ASCII letters, digits and underscores, not starting with a digit,
`_tb_`, `sqlite_` or other reserved engine prefixes, matched exactly against `sqlite_schema`. Errors:

| Status | Code | When |
| --- | --- | --- |
| `400` | `invalid_request` | The table name is malformed or reserved. |
| `400` | `unsupported_table` | `WITHOUT ROWID` tables and virtual tables. |
| `404` | `unknown_database`, `unknown_table` | The database or table does not exist, or the credential cannot reach it. |

## 2a. Filters, columns and move-in/move-out (F2)

`where` is one expression over this table's columns, refused with `400 unsafe_where` unless it uses only:

- this table's stored columns, by bare or double-quoted name (no `table.` qualifier);
- string, number and blob literals, `TRUE`, `FALSE`, `NULL`;
- operators `= == != <> < <= > >= + - * / % || & | ~ << >>`, `AND OR NOT IS IN BETWEEN LIKE GLOB ESCAPE`,
  `CASE WHEN THEN ELSE END`, `CAST(… AS INTEGER|INT|TEXT|REAL|BLOB|NUMERIC)`, `COLLATE BINARY|NOCASE|RTRIM`, parentheses
  and value lists such as `IN (1, 2)`;
- the deterministic functions without side effects `abs coalesce ifnull nullif iif length lower upper ltrim rtrim trim
  substr substring instr replace round typeof hex like glob`.

Anything else is refused before it reaches the engine: subqueries (`SELECT`, `EXISTS`), other functions (`random`,
`date('now')`, `load_extension`, …), parameters, `;`, unknown names and unbalanced parentheses. The accepted text is
rebuilt from its tokens (comments dropped, column names quoted and prefixed with `+`, so comparisons use stored values
without column affinity), then checked against the table once; an expression the engine rejects is
`400 unsafe_where`. A name in `columns` the table does not have, or named twice, is `400 unknown_column`.

Each `(table generation, where, columns)` has its own handle (section 8). Snapshots hold the matching rows. Chunks
compare every captured change's before and after images with `where`:

| Change | Before matched | After matches | Shape op |
| --- | --- | --- | --- |
| insert | – | yes | `insert` |
| update | yes | yes | `update`, skipped when none of the shape's columns changed |
| update | no | yes | `insert` with `"move":"in"` |
| update | yes | no | `delete` with `"move":"out"` |
| delete | yes | – | `delete` |

Other changes produce no op. `row` carries only the shape's columns, in the order `columns` lists them.

## 3. Offsets

| Offset | Meaning |
| --- | --- |
| `-1` | `307` to `snap.<S>.0` of the newest snapshot. `Location` carries `handle`, then `offset`. |
| `snap.<S>.<P>` | Page `P` (from 0) of snapshot `S`. |
| `O` | Every change with `change_id ≤ O` was delivered. `O` is `0` or the `change_id` of a commit record. |

The **head** is the newest commit record's `change_id` (`0` before the first captured write). Offsets are shared by
every table of the database file; commits that do not touch the table still advance the offset.

## 4. Snapshots

- A snapshot `S` holds the table's rows at head `S`. All its pages are built in one read transaction whose head is
  `S`, then stored in `_tb_shape_snapshot`. A snapshot is visible only when its last page is stored.
- A page holds up to 1000 rows or 1 MiB of body in row id order; one larger row travels alone, up to 8 MiB.
  A page also closes after 5 s. `next_offset` is `snap.<S>.<P+1>`; the last page's is the decimal `S`.
- A new snapshot is sealed when the newest one is older than an hour, or has 16 chunks per page past it (at most
  4096), or is below the retention floor. Older snapshots stay servable until retention removes them.
- Snapshot ops are `insert`.

## 5. Chunks

- A chunk at `O` covers whole commits `(O, E]`. It closes before 4096 row operations or 1 MiB of body, or after
  65536 scanned changes; a single commit up to 8 MiB travels alone. A larger commit answers
  `503 shape_too_large`.
- A chunk is sealed once into `_tb_shape_chunk` by the first reader or the background sealer, and never recomputed.
  Its values are the captured after-images, so `values_at` equals `next_offset`.
- An offset that is not a retained commit boundary (the native capture table trimmed below it, or not a commit record) answers
  `409 must_refetch`.

## 6. Body

```json
{"shape_id":"…32 hex…","handle":"h1-…16 hex…","offset":"2","next_offset":"4","values_at":"4","up_to_date":false,
 "changes":[{"offset":"4","ops":[
   {"op":"insert","key":"7","row":{"id":7,"title":"New","data":{"base64":"AAE="}}},
   {"op":"update","key":"3","row":{"id":3,"title":"Renamed","data":null}},
   {"op":"delete","key":"5"}]}]}
```

- One `changes` entry per commit that touched the table, in commit order; its `offset` is the commit's boundary.
- `key` is the row id as a decimal string. `row` holds the shape's columns (by default every stored column in declaration order (virtual generated
  columns are omitted)): null, numbers, strings, or blobs as `{"base64":"…"}` (standard base64), as SQL over REST
  returns them. A non-finite real cannot be served and answers `503 unavailable`.
- An `UPDATE` that changes the row id arrives as `delete` of the old key, then `insert` of the new one.
  The pinned engine captures that insert without an after-image, so the sealer reads the row's values as
  of sealing (or emits `delete` when the row is gone by then). Later changes to that key follow in order, so the
  client converges at every later offset.
- **Apply rule:** apply ops in order. `insert` and `update` set `rows[key] = row`; `delete` removes `rows[key]`.
  A client that applies a snapshot and then every chunk holds exactly the table at `next_offset`.

## 7. Fresh responses and live mode

- A request at the head answers a fresh body: no changes, `next_offset = O`, `up_to_date: true`.
- A chunk whose end is the head is re-framed with `up_to_date: true`.
- `live=true` at the head waits up to 20 s for a commit, then answers the new chunk, or an empty up-to-date body on
  timeout. Behind the head it answers the chunk at once. `TB-Shape-Cursor` is
  `max(floor(unix_seconds / 20), input cursor + 1)`.
- One flight per `(handle, offset, live)`. At most 32 running flights and 4096 joined requests per machine (8 and 1024
  per organization); beyond that `429 shape_busy`. Live requests release the organization's concurrent-request slot
  before they wait.

## 8. Identity and must-refetch

- A `~table:<name>` sentinel row in `_tb_shape_chunk` ([shape-storage.md](shape-storage.md)) records a ULID generation
  per table: the `sqlite_schema` SQL it was issued for, the head it was issued at (`since`), and the database's
  `PRAGMA schema_version`. A different SQL, or
  no row, issues a new generation. A different schema version rechecks the captured `sqlite_schema` changes after
  `since`: one that names the table (`DROP TABLE` followed by an identical `CREATE TABLE`, `CREATE INDEX` on it)
  issues a new generation; otherwise the row only records the new schema version.
- `fields = organization ‖ 0x00 ‖ project ‖ 0x00 ‖ database ‖ 0x00 ‖ incarnation ‖ 0x00 ‖ table ‖ 0x00 ‖ generation ‖ 0x00 ‖ "2" ‖ 0x00 ‖ filter`,
  where `filter` is the rebuilt `where` (empty when absent) ‖ 0x00 ‖ the column names joined by 0x1F (empty without
  `where` or `columns`);
  `handle = "h1-" ‖ hex(SHA-256("TB-SHAPE-HANDLE-V2" ‖ 0x00 ‖ fields))[0..16]`,
  `shape_id = hex(SHA-256("TB-SHAPE-V2" ‖ 0x00 ‖ fields))[0..32]`.
- Any DDL that names the table ends the lineage, so rows captured under one definition are never decoded under
  another: every request resolves the current generation first, and a chunk being sealed stops before a captured
  `sqlite_schema` change for the table.
- A copy, restore or rewrite renews the incarnation and so every handle.
- `409 must_refetch` answers carry the current handle in `TB-Shape-Handle` and in
  `{"error":{"code":"must_refetch","message":…,"handle":…}}`. Restart at `offset=-1`.

## 9. Headers, ETag and caching

| Header | On | Value |
| --- | --- | --- |
| `ETag` | `200`, `304` | `"<shape_id>:<handle>:<offset>:<next_offset>:<digest>"`, digest = hex of the first 8 bytes of SHA-256 over the body |
| `TB-Shape-Handle` | `200`, `304`, `307`, `409` | Current handle |
| `TB-Shape-Next-Offset` | `200`, `304` | `next_offset` |
| `TB-Shape-Up-To-Date` | `200`, `304` | `true` / `false` |
| `TB-Shape-Cursor` | live | Section 7 |

Every origin answer is `Cache-Control: private, no-store` with `Vary: Authorization`. `If-None-Match` matching the
tag answers `304`. Shared caching uses only sealed TBS1 ciphertext in R2 behind Cloudflare
([free-reads-cdn.md](free-reads-cdn.md), [pivot/r2-shape-runtime.md](pivot/r2-shape-runtime.md)).

## 10. Encrypted objects, keys and manifest

Sealed snapshot pages and chunks are mirrored as TBS1 ciphertext, unchanged from v1: AES-256-GCM frames under a
per-object key derived from the database data key, a 10-minute epoch and the binding `handle ‖ 0x00 ‖ offset`
([shape-storage.md](shape-storage.md)).

- `GET …/tables/{table}/shape/keys?handle=&epoch=&key_id=` releases the epoch key for the current handle, under the
  same scope and tenancy checks; `private, no-store`. Stale handles and unretained pairs answer `409 must_refetch`;
  an unverified catalog answers `503 catalog_stale`.
- `GET …/tables/{table}/shape/manifest?handle=&offset=` lists up to 8 mirrored objects starting at `offset`, each with
  `offset`, `next_offset`, `binding`, `object`, `url`, `expires_at`, `epoch`, `key_id`, `sha256` and `size`. An
  empty list means read the origin. Clients check size, digest, header, key and body before applying anything, and
  fall back to the origin on any failure.

## 11. Retention and sweep

Chunks and snapshots older than seven days leave in bounded passes and raise the handle's floor; the newest complete
snapshot always keeps every page. Handles of retired generations are swept
([shape-retention.md](shape-retention.md), [shape-sweep.md](shape-sweep.md)). Trimming the native capture table itself belongs to
PIVOT F5, tied to the shape floor and the backup position.

## 12. Proof

`tests/TerraBase.Fast/Shapes/TableShapeTests.cs`: a filtered shape with a column list moves rows in and out; a schema
change gives a filtered shape a new handle and `409` for the old one; unsafe `where` and unknown columns are refused.

`tests/TerraBase.Slow/Shapes/TableShapeWorkloadTests.cs`: a shape equals its table after 100,000 random inserts, upserts,
updates, row id changes, replaces and deletes, for a client that synced before the workload, one that bootstrapped in
the middle, and a fresh snapshot afterwards.
