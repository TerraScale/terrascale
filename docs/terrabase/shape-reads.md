# Shape reads, protocol v2 (SQL tables)

A shape is every row of one SQL table of a database. Clients sync it over plain HTTP GET as a sealed snapshot, then
sealed change chunks, then a live tail, Electric style (PIVOT D8). Every response is addressed by `(handle, offset)`,
so identical URLs return identical bytes and any cache can serve many readers for the cost of one.

Filters, column lists and move-in/move-out (PIVOT F2), table-level read grants and end-user tokens (F4), the local
overlay (F7) and region-to-region provisional changes (F8) are later tasks. Protocol v1 (document collections over
`_tb_feed`) is gone.

## 1. Source: the home database's change capture

- Every TerraBase write connection runs Turso change capture (`PRAGMA capture_data_changes_conn('after')`). Each
  committed transaction appends its row changes, with after-images, to the database file's `turso_cdc` table in the
  same transaction, then one commit record (`change_type = 2`).
- PIVOT D8 asks for `full` mode (before images too, for F2 move-in/move-out). On the pinned Turso 0.8.2, `full`
  breaks every `INSERT … ON CONFLICT DO UPDATE` with an I/O error, because the upsert reads its before image after
  the write; upstream fixed this in `26dbb464` (in v0.9.0-pre.2). Capture moves to `full` once that fix is in the
  pinned native build. Shapes need only after-images: keys are row ids, a row id change is captured as delete then
  insert, and a re-created table's `CREATE` is captured with its after-image.
- Shapes read only `turso_cdc` and the table itself. The document feed (`_tb_feed`) is not read.
- Only the database's home serves and seals shapes. Region replicas are not served yet (`replicas_unavailable`), so
  every sealed chunk holds only home-committed, home-confirmed changes. When region writes arrive (PIVOT R4 to R7),
  they reach shapes through the home apply, which commits through the same capture.
- `turso_cdc` uses `AUTOINCREMENT` change ids, so ids are never reused, also after retention trims old rows.

## 2. Route

`GET /v1/projects/{project}/databases/{database}/tables/{table}/shape?handle=&offset=&live=&cursor=`

Client listener, TLS, `documents:read`, OpenAPI tag `Live`. End-user tokens are refused until F4.

| Parameter | Value |
| --- | --- |
| `offset` | Required: `-1`, `snap.<S>.<P>`, or a decimal commit boundary `O`. |
| `handle` | Required unless `offset=-1`. A bootstrap with a handle is `409 must_refetch`. |
| `live` | `true`, only with a decimal `O`. |
| `cursor` | Only with `live=true`: the decimal live bucket. |

Validation answers `400 invalid_request`: unknown or repeated parameters, non-canonical decimals (no sign or leading
zeros, at most `9223372036854775807`), a malformed handle, `live` without a decimal offset, `cursor` without `live`.

`table` names an ordinary rowid table: 1 to 63 ASCII letters, digits and underscores, not starting with a digit,
`_tb_`, `turso_` or `sqlite_`, matched exactly against `sqlite_schema`. Errors:

| Status | Code | When |
| --- | --- | --- |
| `400` | `invalid_request` | The table name is malformed or reserved. |
| `400` | `unsupported_table` | `WITHOUT ROWID` tables and virtual tables. |
| `404` | `unknown_database`, `unknown_table` | The database or table does not exist, or the credential cannot reach it. |

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
- An offset that is not a retained commit boundary (`turso_cdc` trimmed below it, or not a commit record) answers
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
- `key` is the row id as a decimal string. `row` holds every stored column in declaration order (virtual generated
  columns are omitted): null, numbers, strings, or blobs as `{"base64":"…"}` (standard base64), as SQL over REST
  returns them. A non-finite real cannot be served and answers `503 unavailable`.
- An `UPDATE` that changes the row id arrives as `delete` of the old key, then `insert` of the new one.
  On the pinned Turso 0.8.2 that insert is captured without an after-image, so the sealer reads the row's values as
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
- `fields = organization ‖ 0x00 ‖ project ‖ 0x00 ‖ database ‖ 0x00 ‖ incarnation ‖ 0x00 ‖ table ‖ 0x00 ‖ generation ‖ 0x00 ‖ "2"`;
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
per-object key derived from the project data key, a 10-minute epoch and the binding `handle ‖ 0x00 ‖ offset`
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
([shape-retention.md](shape-retention.md), [shape-sweep.md](shape-sweep.md)). Trimming `turso_cdc` itself belongs to
PIVOT F5, tied to the shape floor and the backup position.

## 12. Proof

`tests/TerraBase.Slow/Shapes/TableShapeEqualityTests.cs`: a shape equals its table after 100,000 random inserts, upserts,
updates, row id changes, replaces and deletes, for a client that synced before the workload, one that bootstrapped in
the middle, and a fresh snapshot afterwards.
