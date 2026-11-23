# MySQL and MariaDB type and syntax mapping

Use this as a decision ledger, not an automatic textual rewrite. TerraScale
documents the SQLite dialect and values as null, 64-bit integers, doubles, text
and blobs: [SQL][sql]. The mappings below are migration designs using those
documented primitives. A MySQL spelling that parses is not proof that it retains
precision, constraints, collation or timezone behavior. Test chosen declarations
on the rehearsal database and compare canonical values.

## Types and constraints

| MySQL type or declaration | Destination representation or workaround | Documentation |
| --- | --- | --- |
| Signed TINYINT/SMALLINT/MEDIUMINT/INT/BIGINT | `INTEGER`; preserve signed range in conversion and explicit CHECKs where required. Preserve BIGINT values without floating-point conversion. | [MySQL integers][integers]; [TerraScale SQL][sql] |
| Unsigned integer through INT | `INTEGER` with the source lower/upper bounds enforced explicitly; there is no documented unsigned primitive. | [MySQL integers][integers]; [TerraScale values][sql] |
| BIGINT UNSIGNED | Range-check first. Values beyond signed 64-bit need canonical decimal `TEXT` or a redesigned key. Convert every referencing column identically. Decimal text requires explicit numeric ordering in the app; lexical ordering alone is wrong. | [MySQL integers][integers]; [TerraScale values][sql] |
| AUTO_INCREMENT | Preserve existing IDs using explicit inserts. For future rowid allocation use exactly `INTEGER PRIMARY KEY`; choose and rehearse an ID policy that also preserves source high-water requirements. Do not append MySQL AUTO_INCREMENT clauses. | [MySQL AUTO_INCREMENT][auto]; [TerraScale generated ID/result][http] |
| Deleted AUTO_INCREMENT high-water values | `max(id)` may be below the next source ID. Capture source AUTO_INCREMENT metadata; choose application-managed IDs above that watermark or a reviewed SQLite AUTOINCREMENT design. Do not write to internal sequence tables; internal names are refused. | [MySQL AUTO_INCREMENT][auto]; [TerraScale refused statements][sql] |
| BOOL/BOOLEAN/TINYINT(1) | Audit actual values; width `(1)` does not prove booleans. Normalize genuine booleans to INTEGER 0/1, retain NULL separately and enforce the selected domain. | [MySQL numeric overview][numeric]; [TerraScale HTTP argument types][http] |
| DECIMAL/NUMERIC | For exact arithmetic, use a proven bounded scaled INTEGER with fixed scale. Otherwise preserve canonical decimal TEXT and calculate with an exact decimal library. REAL/double is unsuitable for exact money. Check overflow in totals, not only individual rows. | [MySQL fixed point][decimal]; [TerraScale value types][sql] |
| FLOAT/DOUBLE | REAL/double when the business accepts binary floating point. Verify special/out-of-range values and rounding; do not compare decimal text dumps byte-for-byte to binary results. | [MySQL floating point][float]; [TerraScale values][sql] |
| CHAR/VARCHAR/TINYTEXT/TEXT/MEDIUMTEXT/LONGTEXT | TEXT; preserve UTF-8 bytes intentionally, decide trimming and length constraints, and size large values against serialized request limits. Do not retain CHARACTER SET/COLLATE declarations without translation. | [MySQL string types][strings]; [TerraScale SQL][sql], [limits][limits] |
| utf8mb4 case/accent-insensitive collation | No MySQL collation compatibility is documented. Store an application-normalized search/unique key and index it; version normalization and detect collisions before loading. ASCII LIKE case-insensitivity is not Unicode collation equivalence. | [MySQL collations][collations]; [TerraScale SQL dialect][pg], [indexes][indexes] |
| BINARY/VARBINARY/*BLOB | BLOB; decode source binary values exactly. HTTP binds `{"base64":"…"}`, not a raw Buffer JSON object or a guessed hex string. Preserve embedded NUL and distinguish NULL from empty bytes. | [MySQL strings][strings]; [TerraScale HTTP][http] |
| ENUM | TEXT plus a reviewed domain CHECK or lookup FK. Export labels, not numeric ordinals; replace MySQL ENUM ordinal sorting with an explicit rank. Audit empty-string error values. | [MySQL ENUM][enum]; [TerraScale tables/foreign keys][sql] |
| SET | Normalize into a child/junction table, or use documented JSON-as-TEXT functions with explicit validation. Preserve membership semantics; comma-separated text alone is not a set constraint. | [MySQL SET][set]; [TerraScale JSON][sql] |
| JSON | TEXT containing valid JSON. Preserve JSON null versus SQL NULL and numeric precision. Rewrite path/extraction/update functions and check expression-index semantics. | [MySQL JSON][json]; [TerraScale JSON][sql], [indexes][indexes] |
| DATE | Canonical `YYYY-MM-DD` TEXT or another explicitly chosen encoding. Reject or map zero/invalid dates with a recorded rule; never let a runtime silently repair them. | [MySQL dates][dates]; [TerraScale values][sql] |
| DATETIME(fsp) | Usually wall-clock TEXT: no source timezone conversion is implied. If the application means an instant, resolve the actual source zone/DST, then normalize to a fixed UTC format. Preserve fractional precision. | [MySQL DATETIME/TIMESTAMP][dates]; [TerraScale values][sql] |
| TIMESTAMP(fsp) | Export in UTC and encode as fixed-width UTC TEXT or integer epoch units. MySQL converts using session time zone, unlike DATETIME. Record unit and precision; verify default and driver conversion behavior separately. | [MySQL timestamps][dates], [mysqldump --tz-utc][dump]; [TerraScale values][sql] |
| TIME/YEAR | TIME may be a duration beyond 24 hours: use integer units or canonical duration TEXT. YEAR needs an explicit integer/text policy. Do not force either into an instant. | [MySQL temporal types][temporal]; [TerraScale values][sql] |
| BIT(n) | Normalize to bounded INTEGER or BLOB by width, with explicit bit ordering. Do not assume the MySQL driver returns a number. | [MySQL BIT][bit]; [TerraScale values][sql] |
| Spatial types | No equivalent spatial extension is documented. Preserve WKB as BLOB or GeoJSON as TEXT and move spatial queries to an application/service. Mark spatial predicates as an unresolved gap until replaced. | [MySQL spatial][spatial]; [TerraScale SQL][sql] |
| Generated columns/default functions | Recreate only reviewed SQLite expressions that work in rehearsal; otherwise calculate on every application write and verify backfilled values. No automatic translator is documented. | [MySQL generated columns][generated]; [TerraScale import][import] |
| Vitess schemas without FKs | Discover logical relationships from application/schema history, audit orphans, and declare foreign keys on the final schema. To add after import, rebuild/copy in bounded batches as the main skill describes. Enforcement is always on. | [PlanetScale foreign keys][ps-fk]; [TerraScale SQL][sql] |

## Query and DDL rewrites

| MySQL syntax or behavior | Destination equivalent or required design | Documentation |
| --- | --- | --- |
| Backtick identifiers, database-qualified names | Prefer double-quoted identifiers; choose collision-free flat table names. Parse identifiers without rewriting literals. Never bind an identifier as a value parameter. | [MySQL identifiers][identifiers]; [TerraScale SQLite dialect][sql] |
| `ENGINE=InnoDB`, AUTO_INCREMENT table option, charset clauses | Omit source storage options after translating their semantics; preserve allocation and text-comparison policy elsewhere. | [MySQL CREATE TABLE][create]; [TerraScale import][import] |
| `LIMIT offset,count` | Rewrite to `LIMIT count OFFSET offset`; for reconciliation and hot paths, prefer stable primary-key keyset pages. Preserve deterministic ORDER BY and tie-breakers. | [MySQL SELECT][select]; [TerraScale SQLite dialect][sql], [limits][limits] |
| `INSERT ... ON DUPLICATE KEY UPDATE` | Use reviewed SQLite `INSERT ... ON CONFLICT (chosen_unique_key) DO UPDATE SET ... = excluded...`. Select one intended conflict target and handle other unique violations; MySQL's any-unique-key behavior does not map automatically. | [MySQL upserts][upsert]; [TerraScale SQLite dialect][sql] |
| `VALUES(col)` in upserts | Reference `excluded.col` in the rewritten conflict action, preserving the intended source input and update expression. Do not replace occurrences inside literals. | [MySQL upserts][upsert]; [TerraScale SQLite dialect][sql] |
| `REPLACE` | Prefer explicit INSERT/ON CONFLICT UPDATE for update semantics. Delete-and-insert behavior can change row IDs, fire triggers or cascade; preserve it only when deliberately tested. | [MySQL REPLACE][replace]; [TerraScale foreign keys/triggers][sql] |
| `ON UPDATE CURRENT_TIMESTAMP` | Set `updated_at` explicitly in every application update/upsert, using the chosen precision/time format, or implement a reviewed SQLite trigger. Do not assume the MySQL column clause carries over. | [MySQL timestamp initialization][on-update]; [TerraScale triggers][sql] |
| `NOW()`, `UTC_TIMESTAMP()`, `DATE_FORMAT`, `CONVERT_TZ` | Compute timestamps/formatting in the application, or use individually verified SQLite expressions. Wire-only `now()` translation does not apply to HTTP. | [MySQL date functions][date-functions]; [TerraScale wire spellings][pg] |
| `IFNULL`, `IF`, `CONCAT`, JSON operators, SQL-mode coercion | Audit by expression and return type; use portable CASE/COALESCE and reviewed SQLite expressions or app computation. Do not infer that identical function names have identical semantics. | [MySQL functions][functions]; [TerraScale dialect][sql] |
| `INSERT IGNORE` | Decide which errors were ignored, then handle the specific desired conflict. Do not replace with indiscriminate error suppression; validate rejected rows. | [MySQL INSERT][insert]; [TerraScale error codes][http] |
| `SELECT ... FOR UPDATE` and lock-based workflows | Redesign and rehearse as a short atomic conditional-write batch or an interactive transaction on a documented transport. No MySQL row-lock compatibility is documented. | [MySQL locking reads][locking]; [TerraScale transactions][sql] |
| `BEGIN`/`COMMIT` in a dump or HTTP client | Remove wrappers for CLI/HTTP import; one HTTP request is already atomic. Interactive WebSocket/wire transactions last at most 5 seconds. | [MySQL transactions][transactions]; [TerraScale transactions][http], [limits][limits] |
| `ALTER TABLE ... ADD CONSTRAINT` | Rebuild with the reviewed final declaration; preflight orphans and dependencies. Split data copy into bounded requests; never disable foreign keys. | [MySQL ALTER TABLE][alter]; [TerraScale dialect/enforcement][sql] |
| FULLTEXT/spatial index, MySQL prefix indexes | No compatible specialized index behavior is documented. Replace query/index design and prove it using ordinary/partial/expression indexes where appropriate. | [MySQL indexes][mysql-index]; [TerraScale indexes][indexes] |
| Stored routines, events and MySQL triggers | Move orchestration to application/jobs; port only reviewed trigger bodies to documented SQL support. Never import DEFINER/security metadata as authorization. | [MySQL stored objects][objects]; [TerraScale SQL][sql] |
| mysql2 insertId/affectedRows, PlanetScale result metadata | HTTP returns `last_insert_rowid` and `rows_affected`; rows are arrays with column metadata. Verify business logic instead of equating MySQL affected-row counts to destination counts. | [MySQL upsert affected rows][upsert]; [TerraScale HTTP response][http] |

## Worked semantic conversion

Source DDL:

```sql
CREATE TABLE customers (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  email VARCHAR(255) COLLATE utf8mb4_0900_ai_ci NOT NULL UNIQUE,
  balance DECIMAL(12,2) NOT NULL,
  status ENUM('active','disabled') NOT NULL,
  updated_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
    ON UPDATE CURRENT_TIMESTAMP(6)
);
```

Example target policy: all IDs have been proven to fit signed 64-bit; balance is
stored as cents; the application supplies UTC timestamps and a versioned normalized
email key. The original email is preserved for display. Normalization must be
chosen for the application's needs and must not be labeled a perfect emulation
of the MySQL collation.

```sql
CREATE TABLE customers (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL,
  email_key TEXT NOT NULL UNIQUE,
  balance_cents INTEGER NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('active', 'disabled')),
  updated_at TEXT NOT NULL
);
```

Translate decimal text to cents using exact decimal parsing, not multiplication
of a binary floating-point value. Export TIMESTAMP values with UTC session settings;
produce one canonical fixed-width format including fractional seconds. On every
write, the application supplies `email_key` and `updated_at`. Test INSERT, UPDATE,
UPSERT, bulk jobs and error paths against this policy. New IDs must also satisfy
the captured source high-water rule. These are chosen application conventions
over the documented [TerraScale value types][sql], not automatic conversions.

[sql]: https://docs.terrascale.tech/concepts/sql
[pg]: https://docs.terrascale.tech/guides/postgres-clients
[http]: https://docs.terrascale.tech/guides/sql-http
[import]: https://docs.terrascale.tech/guides/import
[limits]: https://docs.terrascale.tech/limits
[indexes]: https://docs.terrascale.tech/guides/indexes
[integers]: https://dev.mysql.com/doc/refman/8.4/en/integer-types.html
[auto]: https://dev.mysql.com/doc/refman/8.4/en/example-auto-increment.html
[numeric]: https://dev.mysql.com/doc/refman/8.4/en/numeric-type-syntax.html
[decimal]: https://dev.mysql.com/doc/refman/8.4/en/fixed-point-types.html
[float]: https://dev.mysql.com/doc/refman/8.4/en/floating-point-types.html
[strings]: https://dev.mysql.com/doc/refman/8.4/en/string-types.html
[collations]: https://dev.mysql.com/doc/refman/8.4/en/charset-collation-effect.html
[enum]: https://dev.mysql.com/doc/refman/8.4/en/enum.html
[set]: https://dev.mysql.com/doc/refman/8.4/en/set.html
[json]: https://dev.mysql.com/doc/refman/8.4/en/json.html
[dates]: https://dev.mysql.com/doc/refman/8.4/en/datetime.html
[temporal]: https://dev.mysql.com/doc/refman/8.4/en/date-and-time-types.html
[bit]: https://dev.mysql.com/doc/refman/8.4/en/bit-type.html
[spatial]: https://dev.mysql.com/doc/refman/8.4/en/spatial-types.html
[generated]: https://dev.mysql.com/doc/refman/8.4/en/create-table-generated-columns.html
[ps-fk]: https://planetscale.com/docs/vitess/foreign-key-constraints
[identifiers]: https://dev.mysql.com/doc/refman/8.4/en/identifiers.html
[create]: https://dev.mysql.com/doc/refman/8.4/en/create-table.html
[select]: https://dev.mysql.com/doc/refman/8.4/en/select.html
[upsert]: https://dev.mysql.com/doc/refman/8.4/en/insert-on-duplicate.html
[replace]: https://dev.mysql.com/doc/refman/8.4/en/replace.html
[on-update]: https://dev.mysql.com/doc/refman/8.4/en/timestamp-initialization.html
[dump]: https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html
[date-functions]: https://dev.mysql.com/doc/refman/8.4/en/date-and-time-functions.html
[functions]: https://dev.mysql.com/doc/refman/8.4/en/functions.html
[insert]: https://dev.mysql.com/doc/refman/8.4/en/insert.html
[locking]: https://dev.mysql.com/doc/refman/8.4/en/innodb-locking-reads.html
[transactions]: https://dev.mysql.com/doc/refman/8.4/en/commit.html
[alter]: https://dev.mysql.com/doc/refman/8.4/en/alter-table.html
[mysql-index]: https://dev.mysql.com/doc/refman/8.4/en/create-index.html
[objects]: https://dev.mysql.com/doc/refman/8.4/en/stored-objects.html
