---
title: Export from Turso
description: Create a single-file SQLite export of a Turso or libSQL database for the proposed one-time import, with the storage-class mapping.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Export from Turso

Create one consistent SQLite file from a Turso, libSQL or SQLite database, check it and upload it with the [import steps](/hi/guides/import#import-step-by-step).

::: warning Pending feature
Import follows the proposed import contract v1 and is not available on any serving release yet. The commands on this page run on your own machine. SQLite commands were checked with SQLite 3.51 and Turso CLI flags with Turso CLI 1.0.26; confirm flags with `--help` for your version. TerraBase never connects to your database.
:::

## 1. Rehearse with synthetic data

Load this fixture into a local file with `sqlite3 rehearsal.db < fixture.sql`, then upload `rehearsal.db` with `FORMAT=sqlite`. To rehearse the Turso export as well, create a throwaway database from the file with `turso db create import-rehearsal --from-file rehearsal.db`, export it as below, and destroy it afterwards.

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

Expect no blocking issues, and `mixed_storage_classes` warnings because `customers.balance` holds a number and the text `'12.50 EUR'`, `customers.active` holds `1` and `'yes'`, and `audit_log.logged_at` holds a Unix time and a date string. The order with key `(1, 2)` becomes document `1:2`, and the `audit_log` rows become documents `1` and `2`.

## 2. Export one consistent file

The import accepts exactly one SQLite database file. Leftover WAL data is not read, so a file uploaded without folding in its `-wal` companion silently loses the newest writes.

**From Turso.** `turso db export` writes a snapshot as `<name>.db` plus a `<name>.db-wal` file beside it. Fold both into one file with SQLite's online backup:

```bash
turso db export YOUR_DATABASE --output-file turso-export.db
sqlite3 turso-export.db ".backup export.db"
chmod 600 export.db
```

Keep `turso-export.db` and `turso-export.db-wal` together until the backup finishes, then delete them. The export uses your own Turso CLI login; TerraBase never receives a Turso token. You do not need `--with-metadata`.

**From a local SQLite or libSQL file:**

```bash
sqlite3 local.db ".backup export.db"
```

`.backup` produces one consistent copy even while the database is in use. Never copy a live database file directly. `VACUUM INTO 'export.db'` also creates a consistent single file, but SQLite documents that a vacuum can renumber the `rowid` of tables without an `INTEGER PRIMARY KEY`, and those rowids become document IDs.

Check the result before you upload it:

```bash
sqlite3 export.db "PRAGMA quick_check"
```

It must print `ok`. A file that fails this check, an encrypted file or a schema SQLite cannot parse, such as libSQL-only syntax, is refused with `unsupported_source_format`. Upload `export.db` with `FORMAT=sqlite`; it must be at most 1 GiB.

## 3. Check what the export will contain

Run these with `sqlite3 export.db` to plan your [options](/hi/guides/import#options). The preview reports the same findings.

**Columns.** Use the names in `exclude` entries as `main.table.column`. `hidden` is `2` or `3` for generated columns.

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

**Tables without a primary key.** A rowid table without a declared primary key uses its `rowid` as the document ID. A `WITHOUT ROWID` table always has a primary key.

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

**Virtual tables.** Views, triggers and virtual tables are skipped. A full-text (FTS5) or R*Tree table stores its index in ordinary shadow tables named after it, such as `notes_fts_data` and `notes_fts_idx`. Exclude those shadow tables, then rebuild the search index from your content.

```sql
SELECT name FROM sqlite_schema WHERE sql LIKE 'CREATE VIRTUAL TABLE%';
```

## Type mapping

Each value maps by its **storage class**, never by the column's declared type.

| Storage class | `mapping` | JSON value |
| --- | --- | --- |
| `INTEGER` | `integer` | Number with exact 64-bit digits |
| `REAL` | `float` | Number; `±Infinity` fails with `invalid_value` |
| `TEXT` | `string` | String |
| `BLOB` | `base64` | Standard base64 string |
| `NULL` | | `null` |
| More than one class in a column | `mixed` | Each value by its own class; reported as a `mixed_storage_classes` warning |

What this means for common column conventions:

- **Booleans** stay numbers `0` and `1`; they do not become JSON `true` and `false`.
- **Dates and times** keep their stored form: ISO 8601 text stays a string, and Unix or Julian-day numbers stay numbers.
- **`NUMERIC` and `DECIMAL`** columns already store non-integers as `REAL`, so exact decimals may have been lost in the source before the export.
- **JSON** stored as `TEXT` becomes a JSON string, not a parsed object. SQLite's binary JSONB becomes a base64 string.
- **libSQL vectors** (`F32_BLOB` and related) are `BLOB`s and become base64 strings. Indexes, including vector indexes, are not imported.
- SQLite stores `NaN` as `NULL`, so it arrives as `null`. JSON readers such as JavaScript lose precision for integers above 2<sup>53</sup> − 1.

</div>
