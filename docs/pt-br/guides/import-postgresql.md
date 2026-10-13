---
title: Export from PostgreSQL
description: Create a pg_dump export for the proposed one-time PostgreSQL import, with a read-only role and the type mapping.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Export from PostgreSQL

Create a plain-text `pg_dump` export, check it and upload it with the [import steps](/pt-br/guides/import#import-step-by-step).

::: warning Pending feature
Import follows the proposed import contract v1 and is not available on any serving release yet. The SQL and `pg_dump` commands on this page run on your own machine and were checked with PostgreSQL 17. TerraBase never connects to your database.
:::

## 1. Rehearse with synthetic data

Create a throwaway database, load this fixture, then export and import it before you touch real data.

```sql
CREATE TABLE customers (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  email text NOT NULL UNIQUE,
  balance numeric(12, 2) NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now(),
  profile jsonb
);

CREATE TABLE orders (
  customer_id bigint NOT NULL REFERENCES customers (id),
  order_no integer NOT NULL,
  total numeric(12, 2) NOT NULL CHECK (total >= 0),
  external_ref bigint,
  score double precision,
  tags text[],
  receipt bytea,
  shipped_at timestamp,
  PRIMARY KEY (customer_id, order_no)
);

CREATE TABLE audit_log (message text, logged_at timestamp); -- no primary key

INSERT INTO customers (email, balance, profile) VALUES
  ('ada@example.com', 10.25, '{"plan": "starter", "beta": true}'),
  ('lin@example.com', 0, NULL);

INSERT INTO orders VALUES
  (1, 1, 19.99, 9007199254740993, 0.1, '{a,NULL,c}', '\x00ff', '2026-01-31 23:59:59.123456'),
  (1, 2, 0, NULL, -2.5, '{}', NULL, NULL),
  (2, 1, 5.00, -1, NULL, NULL, '', NULL);

INSERT INTO audit_log VALUES ('created', '2026-01-01'), ('created', '2026-01-01');
```

Expect two blocking issues: `public.orders` column `tags` is an array (`unsupported_type`), and `public.audit_log` has no primary key (`missing_primary_key`). Resolve them with `exclude: ["public.orders.tags"]` and `missingPrimaryKey: "generate"`. The order with key `(1, 2)` becomes document `1:2`, and `9007199254740993` stays exact.

## 2. Create a read-only role

The export runs with your own credentials. A dedicated read-only role keeps the source unchanged even if those credentials leak. Connect with `psql` as the database owner, replace `app` with your database name and repeat the schema grants for each schema you export. Set `VALID UNTIL` a few days after your planned export.

```sql
CREATE ROLE terrascale_export LOGIN VALID UNTIL '2026-11-01';
GRANT CONNECT ON DATABASE app TO terrascale_export;
GRANT USAGE ON SCHEMA public TO terrascale_export;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO terrascale_export;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO terrascale_export;
ALTER ROLE terrascale_export SET default_transaction_read_only = on;
\password terrascale_export
```

`\password` prompts for the password and sends only its hash, so the password stays out of the SQL text, server logs and shell history.

- The grants cover tables that exist now.
- `default_transaction_read_only` blocks accidental writes, but a session can turn it off; the grants are what keep the source unchanged. In a database created on PostgreSQL 14 or earlier, including one upgraded since, every role can create objects in schema `public` unless that privilege was revoked from `PUBLIC`.
- Managed PostgreSQL services can restrict role options. Use your provider's read-only user flow with the same grants.
- **Row-level security:** `pg_dump` stops with an error rather than export a table whose policies would hide rows from this role. Export such tables with a role that bypasses row security, or exclude them.

Remove the role after you accept the import. Run `DROP OWNED BY` in each database where you granted it access; it revokes those grants.

```sql
DROP OWNED BY terrascale_export;
DROP ROLE terrascale_export;
```

## 3. Check what the export will contain

Run these as the database owner to plan your [options](/pt-br/guides/import#options) before the preview reports them.

**Tables without a primary key** need `missingPrimaryKey: "generate"` or an `exclude` entry.

```sql
SELECT c.oid::regclass AS table_name
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p')
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND n.nspname NOT LIKE 'pg_toast%'
  AND NOT EXISTS (
    SELECT 1 FROM pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'p'
  )
ORDER BY 1;
```

**Column types in use.** Any type missing from the [type mapping](#type-mapping) blocks its table until you exclude the column.

```sql
SELECT format_type(a.atttypid, a.atttypmod) AS column_type, count(*) AS columns
FROM pg_attribute a
JOIN pg_class c ON c.oid = a.attrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p')
  AND a.attnum > 0
  AND NOT a.attisdropped
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND n.nspname NOT LIKE 'pg_toast%'
GROUP BY 1
ORDER BY 2 DESC, 1;
```

**Row-level security.** Tables listed here need the role handling described in step 2.

```sql
SELECT oid::regclass AS table_name, relforcerowsecurity AS forced
FROM pg_class
WHERE relkind IN ('r', 'p') AND relrowsecurity;
```

## Export the database

Run `pg_dump` from a machine that can reach the database. Prompting for the password, or a `~/.pgpass` file with mode `0600`, keeps it out of the command line. Require TLS with certificate verification.

```bash
pg_dump --format=plain --no-owner --no-privileges --encoding=UTF8 -f dump.sql \
  "host=db.example.com dbname=app user=terrascale_export sslmode=verify-full"
chmod 600 dump.sql
```

- **Snapshot.** `pg_dump` reads every table from one transaction snapshot, so the export is consistent without stopping writes. It holds a lock that blocks schema changes on each exported table until it finishes, and a long export holds back vacuum cleanup on the server.
- **Lower the load.** Export from a read replica, a restored backup or a provider branch when you can.
- **Limit the size.** The file must be at most 1 GiB. Use `--table`, `--exclude-table` or `--schema` to split a large database into several imports.
- **Accepted format.** Plain-text UTF-8 SQL with `COPY` data, which is `pg_dump`'s default output. Custom, directory and tar archives, compressed files and `--inserts` or `--column-inserts` dumps are refused. Convert a custom archive offline with `pg_restore -f dump.sql archive.dump`.

Upload `dump.sql` with `FORMAT=postgres_plain`. TerraBase reads only table definitions, enum types, primary key, unique and foreign key constraints, and `COPY` data. Functions, triggers, extensions, `DO` blocks, grants and `\` commands are listed as skipped and never run.

## Type mapping

`\N` (SQL `NULL`) becomes JSON `null` for every type. A type not listed here is `unsupported_type` and blocks its table until you exclude the column.

| PostgreSQL type | `mapping` | JSON value |
| --- | --- | --- |
| `smallint`, `integer`, `bigint` and `serial` forms | `integer` | Number with exact 64-bit digits |
| `real`, `double precision` | `float` | Number as dumped; `NaN` and `±Infinity` fail with `invalid_value` |
| `numeric`, `decimal` | `decimal_string` | String with the exact digits, never rounded; `NaN` stays `"NaN"` |
| `boolean` | `boolean` | `true` or `false` |
| `text`, `varchar`, `char`, `name`, `citext`, enums | `string` | String; `char` padding is kept |
| `uuid` | `string` | Lowercase canonical string |
| `json`, `jsonb` | `json` | The embedded JSON value |
| `bytea` | `base64` | Standard base64 string |
| `date` | `date` | `"YYYY-MM-DD"`; BC dates and `±infinity` fail with `invalid_value` |
| `timestamp` | `timestamp` | `"YYYY-MM-DDThh:mm:ss[.ffffff]"`, no offset |
| `timestamptz` | `timestamptz` | RFC 3339 UTC ending in `Z`, microseconds kept |
| `time`, `timetz`, `interval`, `inet`, `cidr`, `macaddr`, `bit`, `varbit`, `xml` | `text` | String with the exact dumped text |

Unsupported in contract v1: arrays, composite, range and multirange types, domains, geometric types, `money`, `tsvector`, `tsquery`, `pg_lsn` and extension types other than `citext`.

JSON readers such as JavaScript lose precision above 2<sup>53</sup> − 1. Read large `integer` values with a parser that keeps 64-bit integers, or keep them as strings in your application.

</div>
