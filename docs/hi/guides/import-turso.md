---
title: Import from Turso
description: Prepare a read-only token, a consistent snapshot and a type review for a pending one-time Turso import.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Import from Turso

Prepare a Turso (libSQL) database for a one-time import into TerraScale. Read the [import overview](/hi/guides/import) first for the workflow, credential handoff and recovery.

::: warning Pending feature
Turso import is not available yet. The SQL on this page is standard SQLite and was checked with SQLite 3.51. Turso CLI flags can change between versions; confirm them with `--help` before you run them. TerraScale handling marked **pending** awaits the import contract.
:::

## 1. Rehearse with synthetic data

Load this fixture into a local SQLite file, for example `sqlite3 rehearsal.db < fixture.sql`. The Turso CLI can create a throwaway database from that file with `turso db create --from-file`. Delete the rehearsal database when you finish.

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

CREATE TABLE audit_log (message TEXT, logged_at); -- no primary key

INSERT INTO customers VALUES
  (1, 'ada@example.com', 10.25, '2026-01-31T23:59:59Z', 1),
  (2, 'lin@example.com', '12.50 EUR', 1767225599, 'yes');

INSERT INTO orders VALUES
  (1, 1, 19.99, 9007199254740993, x'00ff'),
  (1, 2, 9e999, NULL, NULL),
  (2, 1, 5.0, -1, x'');

INSERT INTO audit_log VALUES ('created', 1767225599), ('created', '2026-01-01');
```

Check how the preview handles the integer above 2<sup>53</sup> − 1, the infinite `REAL`, the text stored in a `NUMERIC` and a `BOOLEAN` column, the two date formats, the binary column and the table without a primary key.

## 2. Create a read-only token

Get the database URL, then create a token that can only read and expires on its own:

```bash
turso db show YOUR_DATABASE --url
turso db tokens create YOUR_DATABASE --read-only --expiration 1d
```

The second command prints the token once. Copy it straight into the import's credential input (**pending**). Do not save it to a file, an `.env` file, a chat or a ticket.

- Use a database token, not a group token. A group token reaches every database in the group.
- Let the token expire rather than revoking it early. `turso db tokens invalidate` invalidates every token for the database, including your application's.

## 3. Check what the import will read

Run these with `turso db shell YOUR_DATABASE` or `sqlite3` on a local copy.

**Declared columns.** SQLite stores a declared type only as an affinity. `hidden` is `2` or `3` for generated columns.

```sql
SELECT m.name AS table_name, p.name AS column_name, p.type AS declared_type,
       p.pk, p.hidden
FROM sqlite_schema AS m
JOIN pragma_table_xinfo(m.name) AS p
WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
ORDER BY m.name, p.cid;
```

**Stored values.** Except in `STRICT` tables, one column can hold several storage classes. Check each column whose values matter:

```sql
SELECT typeof(balance) AS storage_class, count(*) AS row_count
FROM customers
GROUP BY 1;
```

**Tables without a primary key.** Their implicit `rowid` is not a stable identity: `VACUUM` can renumber it. Their record identity is **pending** in the [keys policy](/hi/guides/import#keys-constraints-and-schema-objects).

```sql
SELECT m.name AS table_name
FROM sqlite_schema AS m
WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
  AND NOT EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE pk > 0)
ORDER BY 1;
```

**Foreign key violations.** SQLite enforces foreign keys only on connections that enable them, so a source can contain orphan rows. This lists them:

```sql
PRAGMA foreign_key_check;
```

**Virtual tables.** Full-text (FTS5) and R*Tree tables keep their data in shadow tables, such as `name_data`, that look like ordinary tables. Exclude shadow tables and rebuild the index from its content instead.

```sql
SELECT name FROM sqlite_schema WHERE sql LIKE 'CREATE VIRTUAL TABLE%';
```

## 4. Keep the snapshot consistent

A single SQLite read transaction sees one consistent snapshot. Turso serves the database remotely, so the import must keep every read in one transaction or read from a copy. **Pending:** which mechanism the import uses and how long it can hold a remote read transaction.

The safest source is a point-in-time copy. Where your Turso plan supports branching, `turso db create --from-db` creates one, and `--timestamp` selects an earlier point in time. Otherwise, stop writes to the database during the import. If your application uses embedded replicas, import from the primary database, not from a local replica file.

## Type compatibility

The TerraScale column fills in when the import contract confirms each mapping. Until then, treat a value as unsupported unless the dry-run preview shows a mapping for it.

| Stored value or declared type | What can lose fidelity | TerraScale handling |
| --- | --- | --- |
| `INTEGER` | 64-bit signed; JSON readers such as JavaScript lose precision above 2<sup>53</sup> − 1 | **Pending** |
| `REAL` | IEEE 754 double, including ±Infinity; SQLite stores `NaN` as `NULL` | **Pending** |
| `TEXT` | SQLite does not validate UTF-8, so text can contain invalid sequences or `NUL` characters | **Pending** |
| `BLOB` | Raw bytes need a binary encoding | **Pending** |
| `NULL` | Distinct from an absent column and an empty string | **Pending** |
| Mixed storage classes in one column | The declared type does not constrain non-`STRICT` tables | **Pending** |
| `BOOLEAN` | Stored as `INTEGER` 0 or 1 by convention only | **Pending** |
| Dates and times | No native type: ISO 8601 `TEXT`, Julian-day `REAL` or Unix-time `INTEGER` | **Pending** |
| `NUMERIC`, `DECIMAL` | Non-integers are already stored as `REAL`, so exact decimals can be lost in the source | **Pending** |
| JSON | `TEXT`, or SQLite's binary JSONB stored as `BLOB` | **Pending** |
| libSQL vectors (`F32_BLOB` and related) | Vector encoding and vector indexes | **Pending** |

Also check for `AUTOINCREMENT` counters in `sqlite_sequence`, triggers and views. Their handling is in the [keys policy](/hi/guides/import#keys-constraints-and-schema-objects).

## Count rows for validation

Run against the same frozen database or copy the import read. The query prints one `SELECT` per table; run the statements it prints.

```sql
SELECT 'SELECT ' || quote(name) || ' AS table_name, count(*) AS row_count FROM "'
       || replace(name, '"', '""') || '";'
FROM sqlite_schema
WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
ORDER BY name;
```

Compare each count with the import's validation report, then follow the remaining [validation steps](/hi/guides/import#validate-the-result).

</div>
