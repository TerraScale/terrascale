---
title: Export from Turso
description: Create a single-file SQLite export of a Turso or libSQL database for the proposed one-time import, with the storage-class mapping.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Export from Turso

Create one consistent SQLite file from a Turso, libSQL or SQLite database, check it and upload it with the [import steps](/de/guides/import#import-step-by-step).

::: warning Pending feature
Import follows the proposed import contract v1 and is not available on any serving release yet. The commands on this page run on your own machine. SQLite commands were checked with SQLite 3.51 and Turso CLI flags with Turso CLI 1.0.26; confirm flags with `--help` for your version. No export from a real Turso account has been tested yet. TerraBase never connects to your database.
:::

## 1. Rehearse with synthetic data

Load this fixture into a local file with `sqlite3 rehearsal.db < fixture.sql`. The result is already in rollback-journal mode, so you can upload `rehearsal.db` with `FORMAT=sqlite`. To rehearse the Turso export as well, create a throwaway database from the file with `turso db create import-rehearsal --from-file rehearsal.db`, export it as below, and destroy it afterwards.

```sql
CREATE TABLE customers (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL UNIQUE,
  balance NUMERIC,
  created_at TEXT,
  active BOOLEAN
);

CREATE TABLE orders (
  customer_id INTEGER NOT NULL REFERENCES customers (id),
  order_no INTEGER NOT NULL,
  total REAL,
  external_ref INTEGER,
  receipt BLOB,
  PRIMARY KEY (customer_id, order_no)
) WITHOUT ROWID;

CREATE TABLE audit_log (message TEXT, logged_at); -- no primary key: IDs come from rowid

INSERT INTO customers VALUES
  (1, 'ada@example.com', 10.25, '2026-01-31T23:59:59Z', 1),
  (2, 'lin@example.com', '12.50 EUR', 1767225599, 'yes');

INSERT INTO orders VALUES
  (1, 1, 19.99, 9007199254740993, x'00ff'),
  (1, 2, 0.5, NULL, NULL),
  (2, 1, 5.0, -1, x'');

INSERT INTO audit_log VALUES ('created', 1767225599), ('created', '2026-01-01');
```

Expect no blocking issues, and `mixed_storage_classes` warnings: `customers.balance` maps as `float+string` because it holds `10.25` and `'12.50 EUR'`, while `customers.active` and `audit_log.logged_at` map as `integer+string`. The order with key `(1, 2)` becomes document `1:2`, and the `audit_log` rows become documents `1` and `2`.

## 2. Export one consistent file

The import accepts exactly one SQLite database file in rollback-journal mode. A file in WAL mode is rejected with `wal_mode_source`, because rows still in its `-wal` companion would be missing without any sign in the file itself.

**From Turso.** `turso db export` can write a snapshot as `<name>.db` plus a `<name>.db-wal` file beside it. A read-only `VACUUM INTO` reads both and writes one new rollback-mode file:

```bash
turso db export YOUR_DATABASE --output-file turso-export.db
sqlite3 -readonly turso-export.db "VACUUM INTO 'export.db'"
chmod 600 export.db
```

Run `VACUUM INTO` even when `turso-export.db` already reads as rollback mode, and never upload the exported file directly. SQLite still applies a `-wal` file found beside it, but the uploaded file alone would miss those rows. Keep `turso-export.db-wal` beside `turso-export.db` until `VACUUM INTO` finishes, then delete both. The export uses your own Turso CLI login; TerraBase never receives a Turso token. You do not need `--with-metadata`.

**From a local SQLite or libSQL file:**

```bash
sqlite3 -readonly local.db "VACUUM INTO 'export.db'"
```

`VACUUM INTO` reads one consistent snapshot, even while the database is in use. Always pass `-readonly`. Without it, `sqlite3` checkpoints the WAL into the source file when it closes and deletes the `-wal` file, which changes your source or export pair. With it, the `.db` and `-wal` files stay byte-for-byte unchanged; only a `-shm` index file can appear beside them. Do not use `.backup`: its copy keeps the WAL-mode flag and is rejected. Never copy a live database file directly. A copy taken while another process writes can be torn or stale, and no check of the file alone can detect every such copy.

SQLite documents that a vacuum can renumber the `rowid` of tables without an `INTEGER PRIMARY KEY`. Such tables take their document IDs from the rowids in `export.db`.

Check the journal mode before you upload:

```bash
od -An -tu1 -j18 -N2 export.db
```

It must print `1` twice; `2` twice means WAL mode. A file that is not SQLite 3, or an encrypted file, is rejected with `unsupported_source_format`. Upload `export.db` with `FORMAT=sqlite`; it must be at most 1 GiB.

## 3. Check what the export will contain

Run these with `sqlite3 export.db` to plan your [options](/de/guides/import#options). The preview reports the same findings.

**Columns.** Use the names in `exclude` entries as `main.table.column`; SQLite names match without regard to case. `hidden` is `2` or `3` for generated columns.

```sql
SELECT m.name AS table_name, p.name AS column_name, p.type AS declared_type,
       p.pk, p.hidden
FROM sqlite_schema AS m
JOIN pragma_table_xinfo(m.name) AS p
WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
ORDER BY m.name, p.cid;
```

**Stored values.** Except in `STRICT` tables, one column can hold several storage classes, and each value maps by its own class. Check columns whose values matter:

```sql
SELECT typeof(balance) AS storage_class, count(*) AS row_count
FROM customers
GROUP BY 1;
```

**Tables without a primary key.** A rowid table without a declared primary key uses its `rowid` as the document ID (`idPolicy` `rowid`). A `WITHOUT ROWID` table always has a primary key.

```sql
SELECT m.name AS table_name
FROM sqlite_schema AS m
WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
  AND NOT EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE pk > 0)
ORDER BY 1;
```

**Foreign key violations.** SQLite enforces foreign keys only on connections that enable them, so a database can contain orphan rows. The import records foreign keys without enforcing them and copies orphans unchanged.

```sql
PRAGMA foreign_key_check;
```

**Virtual tables.** Views, triggers, virtual tables, the shadow tables SQLite's FTS3, FTS4, FTS5, R*Tree and Geopoly modules create for them (such as `notes_fts_data`) and SQLite's internal `sqlite_*` tables are excluded automatically and listed in `skipped`. Any other table is imported, even one named like a shadow table. Rebuild a full-text (FTS5) or R*Tree index from its source table after the import. A virtual table that holds its own content gets a `not_imported` warning, and that content is not imported.

```sql
SELECT name FROM sqlite_schema WHERE sql LIKE 'CREATE VIRTUAL TABLE%';
```

To keep such content, copy it into an ordinary table in `export.db`, never in the source, before you create the import:

```sql
CREATE TABLE notes_text AS SELECT rowid AS id, body FROM notes_fts;
```

## Type mapping

Each value maps by its **storage class**, never by the column's declared type.

| Storage class | Class in `mapping` | JSON value |
| --- | --- | --- |
| `INTEGER` | `integer` | Number with exact 64-bit digits |
| `REAL` | `float` | Number; zero is valid; `±Infinity`, nonzero magnitudes below 1e-307 and magnitudes of 1e308 or more are `invalid_value` |
| `TEXT` | `string` | String |
| `BLOB` | `base64` | Standard base64 string |
| `NULL` | | `null` |

A column's `mapping` names the non-`NULL` classes it holds, joined by `+` in the order `integer`, `float`, `string`, `base64`, such as `integer` or `integer+string`. It is `null` when every value is `NULL`. `NULL` never makes a column mixed; two or more non-`NULL` classes give a `mixed_storage_classes` warning.

Analysis maps every row, so an invalid value appears in the preview with its row number before anything is written. Two distinct keys that map to one ID, such as `1` and `'1'` in an untyped key column, are found only during the copy, which then fails with `duplicate_document_id`.

What this means for common column conventions:

- **Booleans** stay numbers `0` and `1`; they do not become JSON `true` and `false`.
- **Dates and times** keep their stored form: ISO 8601 text stays a string, and Unix or Julian-day numbers stay numbers.
- **`NUMERIC` and `DECIMAL`** columns already store non-integers as `REAL`, so exact decimals may have been lost in the source before the export.
- **JSON** stored as `TEXT` becomes a JSON string, not a parsed object. SQLite's binary JSONB becomes a base64 string.
- **libSQL vectors** (`F32_BLOB` and related) are `BLOB`s and become base64 strings. Indexes, including vector indexes, are not imported.
- SQLite stores `NaN` as `NULL`, so it arrives as `null`. JSON readers such as JavaScript lose precision for integers above 2<sup>53</sup> − 1.

</div>
