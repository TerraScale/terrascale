---
title: Import from PostgreSQL
description: Prepare a read-only role, a consistent snapshot and a type review for a pending one-time PostgreSQL import.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Import from PostgreSQL

Prepare a PostgreSQL database for a one-time import into TerraScale. Read the [import overview](/ar/guides/import) first for the workflow, credential handoff and recovery.

::: warning Pending feature
PostgreSQL import is not available yet. The SQL on this page runs in your own PostgreSQL with `psql` and was checked against PostgreSQL 17. TerraScale handling marked **pending** awaits the import contract.
:::

## 1. Rehearse with synthetic data

Create a throwaway database and load this fixture before you touch a real source. It contains values that commonly lose fidelity in a copy.

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
  (1, 1, 19.99, 9007199254740993, 'NaN', '{a,NULL,c}', '\x00ff', 'infinity'),
  (1, 2, 0, NULL, '-Infinity', '{}', NULL, '2026-01-31 23:59:59.123456'),
  (2, 1, 5.00, -1, 0.1, NULL, '', NULL);

INSERT INTO audit_log VALUES ('created', '2026-01-01'), ('created', '2026-01-01');
```

Check how the preview handles the integer above 2<sup>53</sup> − 1, the `NaN` and infinite values, the array with a `NULL` element, the binary column and the duplicate rows in a table without a primary key.

## 2. Create a read-only role

Connect with `psql` as the database owner. Replace `app` with your database name and repeat the schema grants for each schema you import. Set `VALID UNTIL` a few days after your planned import.

```sql
CREATE ROLE terrascale_import LOGIN VALID UNTIL '2026-11-01';
GRANT CONNECT ON DATABASE app TO terrascale_import;
GRANT USAGE ON SCHEMA public TO terrascale_import;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO terrascale_import;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO terrascale_import;
ALTER ROLE terrascale_import SET default_transaction_read_only = on;
\password terrascale_import
```

`\password` prompts for the password and sends only its hash, so the password stays out of the SQL text, server logs and shell history.

- The grants cover tables that exist now. A table created later is not readable by this role.
- `default_transaction_read_only` blocks accidental writes, but a session can turn it off. The grants are what keep the source unchanged. In a database created on PostgreSQL 14 or earlier, including one upgraded since, every role can create objects in schema `public` unless that privilege was revoked from `PUBLIC`.
- Managed PostgreSQL services can restrict role options. Use your provider's read-only user flow with the same grants.

Remove the role when the import is accepted. Run `DROP OWNED BY` in each database where you granted it access; it revokes those grants.

```sql
DROP OWNED BY terrascale_import;
DROP ROLE terrascale_import;
```

## 3. Check what the import will read

Run these as the database owner.

**Row-level security.** The import role sees only rows its policies allow, which is no rows when no policy applies to it. Any table listed here needs a policy for the import role, or a role with `BYPASSRLS` where your provider allows it.

```sql
SELECT oid::regclass AS table_name, relforcerowsecurity AS forced
FROM pg_class
WHERE relkind IN ('r', 'p') AND relrowsecurity;
```

**Tables without a primary key.** Their record identity is **pending** in the [keys policy](/ar/guides/import#keys-constraints-and-schema-objects).

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

**Column types in use.** Compare the result with the [type matrix](#type-compatibility).

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

**Large objects.** A table column of type `oid` that refers to a large object holds only the reference. The content lives in `pg_largeobject` and is not a table row.

```sql
SELECT count(*) AS large_objects FROM pg_largeobject_metadata;
```

**Partitions and inheritance.** A query on a parent table also returns its partitions' or children's rows. Count with `FROM ONLY` to avoid counting a row twice.

## 4. Keep the snapshot consistent

PostgreSQL gives one consistent view inside a single `REPEATABLE READ` transaction. Parallel readers can share it with `pg_export_snapshot()` and `SET TRANSACTION SNAPSHOT`, as `pg_dump --jobs` does. **Pending:** which mechanism the import uses.

Any long snapshot has costs on the source:

- On a primary, it holds back vacuum cleanup until it ends, so dead rows accumulate. Watch `pg_stat_activity` for the import's transaction age.
- On a hot standby, a long read can be cancelled by a recovery conflict. `hot_standby_feedback` and `max_standby_streaming_delay` reduce this.
- `TRUNCATE` and table-rewriting `ALTER TABLE` are not MVCC-safe. A snapshot taken before them can see a not-yet-read table as empty once they commit. Do not run DDL or `TRUNCATE` on the source during the import.

The safest source is a point-in-time copy: a restored backup, a provider branch or a promoted replica. It keeps the import's load off production and gives a resumed job the same data.

## 5. Supply connection details

The import needs host, port, database name, user name and password. Require TLS with certificate verification, `sslmode=verify-full`, using your provider's CA certificate. Enter the password only in the import's credential input (**pending**). Do not paste a connection URI that contains the password anywhere.

## Type compatibility

The TerraScale column fills in when the import contract confirms each mapping. Until then, treat a type as unsupported unless the dry-run preview shows a mapping for it. Extension types (for example PostGIS, `hstore` or `vector`) are unsupported until listed.

| PostgreSQL type | What can lose fidelity | TerraScale handling |
| --- | --- | --- |
| `smallint`, `integer` | Nothing within 32 bits | **Pending** |
| `bigint`, `bigserial` | JSON readers such as JavaScript lose precision above 2<sup>53</sup> − 1 | **Pending** |
| `numeric`, `decimal` | Exact precision and scale; `NaN` and, from PostgreSQL 14, `Infinity` | **Pending** |
| `real`, `double precision` | `NaN`, `Infinity`, `-Infinity` and `-0` have no JSON number form | **Pending** |
| `money` | Text form depends on `lc_monetary` | **Pending** |
| `text`, `varchar(n)`, `char(n)` | `char(n)` pads with trailing spaces; length limits | **Pending** |
| `boolean` | Nothing | **Pending** |
| `bytea` | Raw bytes need a binary encoding | **Pending** |
| `timestamptz` | Microsecond precision; `infinity`, `-infinity` and BC dates | **Pending** |
| `timestamp`, `date`, `time`, `timetz`, `interval` | `timestamp` has no time zone; `interval` mixes months, days and time | **Pending** |
| `uuid` | Nothing | **Pending** |
| `json`, `jsonb` | `json` keeps key order, duplicate keys and whitespace; `jsonb` does not | **Pending** |
| Arrays | Multiple dimensions, non-default lower bounds and `NULL` elements | **Pending** |
| Enums and domains | Allowed labels and domain checks | **Pending** |
| Composite, range and multirange types | Structure and bound inclusivity | **Pending** |
| `inet`, `cidr`, `macaddr`, `bit`, `varbit`, `xml`, `tsvector`, geometric types | Type-specific semantics | **Pending** |
| Large objects (`oid` references) | Content stored outside the table | **Pending** |

## Count rows for validation

Run in `psql` with writes frozen or against the point-in-time copy the import read. `\gexec` runs each generated statement and prints one count per table.

```sql
SELECT format('SELECT %L AS table_name, count(*) AS row_count FROM ONLY %s',
              c.oid::regclass, c.oid::regclass)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r'
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND n.nspname NOT LIKE 'pg_toast%'
ORDER BY 1 \gexec
```

Compare each count with the import's validation report, then follow the remaining [validation steps](/ar/guides/import#validate-the-result).

</div>
