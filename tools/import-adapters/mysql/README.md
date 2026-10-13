# MySQL / MariaDB offline export adapter

Run a dry conversion of the synthetic fixture (Python **3.11+**, standard library only):

```sh
nice -n 19 python3 -I -B tools/import-adapters/mysql/mysql_import.py dry-run \
  tools/import-adapters/mysql/fixtures/synthetic.sql \
  tools/import-adapters/mysql/output-preview.json --source-version mysql-8.4
```

The destination must not exist. `dry-run` parses and validates **every row**, generates
the full artifact in temporary scratch space, and publishes only a JSON conversion
report. It performs no upload, network access, or database connection.

To create the import artifact, choose a **new** destination directory:

```sh
nice -n 19 python3 -I -B tools/import-adapters/mysql/mysql_import.py convert \
  tools/import-adapters/mysql/fixtures/synthetic.sql \
  tools/import-adapters/mysql/output --source-version mysql-8.4
nice -n 19 python3 -I -B tools/import-adapters/mysql/tests.py
```

`output/import.sql` is UTF-8 PostgreSQL plain SQL with `CREATE TABLE`, text
`COPY ... FROM stdin`, and `ALTER TABLE ... ADD` primary/unique/foreign-key metadata.
This is the existing core's `postgres_plain` artifact, **not a new server format**.
`output/report.json` records original source types, sizes, unsignedness, ENUM/SET
members, column attributes, constraints, table options, source/output byte counts and
SHA-256, and per-table source/COPY counts and COPY-data SHA-256. Those data hashes
are hashes of COPY row bytes in source order, **not** TerraBase's document checksums.
The CLI prints only counts or a fixed error code and line; it never prints source values.
Artifacts and local reports can contain sensitive data and schema metadata.
New output files have mode 0600; bundle directories have mode 0700.

## Supported source profile

Profile `mysql-copy-v1` is a **restricted** mysqldump/mariadb-dump text subset for
declared exporter families `mysql-8.0`, `mysql-8.4`, `mariadb-10.11`, and
`mariadb-11.4`. The declaration is required provenance, not version detection or
certification. Tests use synthetic files shaped like that subset; no real exporter,
customer connection, service import, or other external integration has been tested.

Every data-bearing construct must match the supported grammar. A rejected statement
or row rejects the whole conversion; there is no skip-row/exclude policy. In particular:

1. One database, InnoDB ordinary tables, explicit table charset `utf8mb4`, exact
   identifiers, and one primary key per table. Backtick-quoted identifiers support
   doubled backticks and Unicode. Database-qualified names, `USE`, and multi-database
   exports are rejected. Identifiers are at most **63 UTF-8 bytes** to avoid PostgreSQL
   name truncation. Column duplicates are checked without case distinctions.
2. Before schema/data: `SET NAMES utf8mb4`, an explicit `SET SQL_MODE='...'`, and
   `SET TIME_ZONE='+00:00'`. Allowed modes are `NO_AUTO_VALUE_ON_ZERO`,
   `NO_BACKSLASH_ESCAPES`, `STRICT_TRANS_TABLES`, `STRICT_ALL_TABLES`, `NO_ZERO_DATE`,
   `NO_ZERO_IN_DATE`, `ERROR_FOR_DIVISION_BY_ZERO`, `NO_ENGINE_SUBSTITUTION`, and
   `ONLY_FULL_GROUP_BY`; the empty mode is also explicit. `ANSI_QUOTES` and other
   unknown modes are rejected. Restoring unknown saved settings prevents further data
   until known settings are declared again. Text is strictly UTF-8: no BOM or replacement
   decoding. Non-utf8mb4 table/column charset or collation is rejected.
3. `INSERT INTO table (all_columns) VALUES (literal,...),...;` only. The column list
   can reorder columns but must contain every column exactly once. NULL, empty string,
   literal `\N`, empty blob, and missing columns remain distinct: missing columns
   **reject**, never apply defaults. Numeric literals, single-quoted strings, and hex
   binary are supported; INSERT expressions/functions, IGNORE, REPLACE, implicit
   column lists, UPDATE, DELETE, LOAD DATA, SELECT, and transactions reject.
4. Primary, simple unique, foreign, and ordinary index declarations are recognized.
   Foreign targets must be present in the same file, with existing columns and equal
   arity. Prefix/expression/descending indexes, CHECK, generated columns, partitions,
   views, routines, triggers, events, extensions, and arbitrary table options reject.
   Table options are limited to ENGINE, DEFAULT CHARSET/CHARACTER SET, COLLATE, and
   AUTO_INCREMENT. Column options are NULL/NOT NULL, literal DEFAULT or
   CURRENT_TIMESTAMP, ON UPDATE CURRENT_TIMESTAMP, AUTO_INCREMENT, utf8mb4
   CHARACTER SET/COLLATE, and COMMENT. Comments are omitted; other recognized
   attributes are recorded as metadata, never evaluated. Literal defaults are omitted
   from the report; their token kind, decoded UTF-8 byte count and SHA-256 fingerprint
   are retained. NULL/CURRENT_TIMESTAMP defaults retain their symbolic spelling.
5. Ordinary comments are tokenized. Executable comments are limited to SET-only
   wrappers `/*!40014 ... */`, `/*!40101 ... */`, `/*!40103 ... */`,
   `/*!40111 ... */`, and `/*!50503 ... */`, which are active in all declared
   exporter families. Conditional data/schema statements, other version gates and
   multiple statements inside a wrapper **reject**; no patch version is guessed.
   Supported SET save/restore statements cover charset/collation, SQL_MODE,
   TIME_ZONE, FOREIGN_KEY_CHECKS, UNIQUE_CHECKS and SQL_NOTES. The exact MariaDB marker
   `/*M!999999\- enable the sandbox mode */` is counted and ignored as a client safety
   marker for declared MariaDB sources only. Other shell/client/meta commands and
   unknown SET statements reject.

The supported rows/types are:

| MySQL/MariaDB source | Output PostgreSQL type → core JSON value | Validation / loss |
| --- | --- | --- |
| TINYINT, SMALLINT, MEDIUMINT, INT/INTEGER, BIGINT, signed or unsigned | `bigint` → exact JSON integer, except unsigned BIGINT | Original signed/unsigned range checked. TINYINT(1) is an integer, not a boolean. Display width is metadata. |
| BIGINT UNSIGNED | `text` → digit string | Full 0…18446744073709551615 preserved without signed overflow. |
| DECIMAL/NUMERIC(p,s), including UNSIGNED | `numeric` → exact decimal string | Precision 1…65; scale 0…min(30,p); exactly s fractional digits; no exponents or rounding. Default declaration means (10,0). |
| CHAR/VARCHAR, TINYTEXT/TEXT/MEDIUMTEXT/LONGTEXT | `text` → string | CHAR/VARCHAR character length and TEXT-family byte bounds checked; NUL rejected. Exported CHAR padding is not reconstructed. |
| ENUM, SET | `text` → string | Exact ENUM member; SET members unique and in declared order. Empty ENUM allowed only if declared; empty SET allowed. Numeric error sentinels reject. Membership semantics are lost after import. |
| BINARY/VARBINARY, TINYBLOB/BLOB/MEDIUMBLOB/LONGBLOB | `bytea` → standard base64 string | **Hex literal only**, even length, original byte bound checked; BINARY must have exact declared length. Bytes and padding are preserved. |
| DATE | `date` → `YYYY-MM-DD` | Valid Gregorian date, year 1000…9999. All zero/partial-zero/invalid dates reject. |
| DATETIME(fsp) | `timestamp` → `YYYY-MM-DDThh:mm:ss[.ffffff]` | Calendar validation, fsp 0…6, no timezone added or inferred. |
| TIMESTAMP(fsp) | `timestamptz` → RFC 3339 UTC string | UTC declaration mandatory. Conservative common range 1970-01-01 00:00:01…2038-01-19 03:14:07.999999; other values reject even on MariaDB versions supporting a wider range. |
| TIME(fsp) | `text` → exact string | Signed duration through ±838:59:59.999999, minute/second/fraction validation; not a time of day or instant. |

FLOAT/DOUBLE/REAL, JSON, BIT, YEAR, geometry/spatial and every unlisted type reject.
Numeric primary keys must be integer types; string/ENUM/SET keys are also supported.
Decimal, temporal and binary primary keys reject. MySQL PRIMARY KEY implies NOT NULL
and that implication is preserved even when the column omits `NOT NULL`.

Strings honor the declared `NO_BACKSLASH_ESCAPES` mode, doubled single quotes, and
MySQL `\0`, `\b`, `\n`, `\r`, `\t`, `\Z`, `\\`, `\'`, `\"`. Unknown escapes reject.
Literal NUL is allowed in binary, rejected in text. COPY escaping is applied after
decoding, so tabs/newlines/backslashes and a literal COPY terminator cannot create rows.

## Export consistency and preparation

An operator exports locally using their own installed tools and approved access.
The adapter itself accepts files only and has no credential arguments. The following
are **export-profile examples**, not commands exercised by these tests:

```sh
# MySQL client 8.0/8.4; one database, only transactional InnoDB tables.
mysqldump --compact --single-transaction --quick --skip-lock-tables \
  --complete-insert --hex-blob --default-character-set=utf8mb4 --tz-utc \
  --skip-triggers --skip-routines --skip-events --no-tablespaces \
  --set-gtid-purged=OFF example_database > snapshot.raw.sql

# MariaDB client 10.11/11.4; GTID option above is MySQL-specific.
mariadb-dump --compact --single-transaction --quick --skip-lock-tables \
  --complete-insert --hex-blob --default-character-set=utf8mb4 --tz-utc \
  --skip-triggers --skip-routines --skip-events --no-tablespaces \
  example_database > snapshot.raw.sql
```

`--compact` removes dump replay DROP/LOCK/index-toggle boilerplate and SET NAMES.
Add explicit declarations to a **new profile file**, preserving the raw export:

```sh
printf '%s\n' "SET NAMES utf8mb4;" "SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';" \
  "SET TIME_ZONE='+00:00';" > declarations.sql
cat declarations.sql snapshot.raw.sql > snapshot.profile.sql
```

Use that SQL mode only for dump literal syntax produced in the ordinary escaped mode.
If a reviewed export uses unescaped strings, declare `NO_BACKSLASH_ESCAPES` instead;
never guess from values or edit escape sequences to make validation pass. Recognized
dump SET statements can change the mode explicitly. Inspect the schema for the exact
subset above before converting. An exporter may emit unsupported syntax even with
these options; rejection requires a new supported export or another adapter, not
deleting statements or silently discarding rows. Tables with unsupported schema are
not automatically omitted. These commands omit routines/triggers/events intentionally;
none of those objects is transferred.

Use one complete `--single-transaction` invocation covering all tables, prohibit DDL
during export, and verify the exporter exit status **before** accepting its file.
InnoDB snapshot consistency does not cover nontransactional tables. No CLI can prove
that an apparently complete SQL file came from a consistent or complete database
snapshot. Keep a source-side table/row inventory from the same snapshot/export and
compare it with the report. Do not split table exports from different snapshots.
Keep the finished export unchanged and away from concurrent writers; the adapter
opens a regular file read-only, rejects source symlinks, checks file metadata, and
re-hashes the same open file after parsing. Existing destination files/directories
are refused, never replaced. Failure leaves no published conversion output.

## Output semantics and limits

All tables map to `public."original table"`; columns retain exact spelling/order.
Tables and constraints are emitted in sorted table order; rows retain their order in
the export, including across multiple INSERT statements. Identical bytes and the same
source declaration produce identical artifact/report bytes, without timestamps or
random IDs. Schema/count checks include all declared tables, even empty ones, all
columns, primary/foreign-key references, row widths, exported row counts, and output
COPY-row counts.

Collection names and primary-key document IDs follow [core contract v1 revision 3](../../../docs/terrabase/import.md)
at commit `62f251f6beb8c4075dbba6f59a6a97c66487b754` (PR #167; compatible with the
provided contract reference `54c0c4d9`). Collection collisions/reserved names reject.
One key uses canonical integer digits or the literal string. Composite components
escape `%` to `%25` and `:` to `%3A` before joining with `:`. Empty/control-character
IDs and IDs exceeding 256 UTF-16 code units reject. Duplicate resulting IDs reject
locally using a disk-backed scratch index. No generated row/document IDs are needed.

TerraBase does **not** enforce the original constraints. The adapter does not emulate
collation, uniqueness under source comparison rules, foreign-key actions, defaults,
indexes, AUTO_INCREMENT or ON UPDATE behavior. Primary/unique/foreign constraints and
NOT NULL are emitted as core metadata; ordinary indexes and other recognized
attributes appear only in the report. Collation, type-specific comparison behavior,
CHAR padding removed by the exporter, and original MySQL database namespace are lost.
The operator must review those warnings and the future core preview separately.

Fixed bounds: source and output each **1 GiB**, 1000 tables, 1600 columns/table, a
1 MiB interpreted schema statement, a 2 MiB individual token, a 16 MiB decoded row,
and a 1 MiB mapped JSON document. Retained schema metadata is capped at **512 KiB**
of compact ASCII JSON across all tables, and the final report at **4 MiB**. Literal
default payloads are fingerprinted instead of retained; reports serialize incrementally.
ENUM declarations have at most 4096 members; SET has at most 64. Scalar rows have no nested JSON, so the core's
16-level/4096-value bounds are satisfied by that column cap. Extended INSERT
statements can exceed 1 MiB: tuples are parsed incrementally from 64 KiB UTF-8 chunks.
Rows are spooled per table; the uniqueness index uses SQLite with **fixed trusted
scratch SQL and bound parameters only**. No export statement/schema/function is
executed by SQLite, MySQL, PostgreSQL, a shell or any other engine. Scratch space is
beside the destination, needs space for rows, the uniqueness index and final artifact,
and is cleaned on ordinary success/failure; a process crash may leave `.mysql-*`.

This CLI's report is not the core's preview or approval. The future core can still
reject its preview size or another limit; its required dry-run/count/checksum
validation remains necessary. Integration is deliberately not wired into core, SDK,
dashboard, jobs, CI or deployment.

## Synthetic verification

[fixtures/synthetic.sql](fixtures/synthetic.sql) covers maximum unsigned BIGINT,
DECIMAL(65,30), NULL versus empty/literal `\N`, Unicode, escaped strings, binary NUL,
empty versus missing binary, enums/sets, durations, microseconds, original identifiers,
reordered columns, multi-row inserts, and primary/unique/foreign-key metadata.
[fixtures/expected.sql](fixtures/expected.sql) is the resulting 2-table/3-row artifact.
The parallel subprocess suite uses isolated temporary resources and independently
decodes COPY values. It also checks determinism, dry-run equivalence, read-only input,
no overwrite/partial publication, integer/decimal boundaries, encoding, SQL modes,
zero dates/timezone/precision, malformed input, rejected routines/expressions, duplicate
IDs, a Unicode record crossing many chunks, and a multi-row INSERT over 1 MiB.
Regressions also reject inactive/ambiguous conditional statements, bound aggregate
metadata, and verify that multi-megabyte default payloads become small fingerprints.

Required repository-wide .NET checks and external/service integration are not part
of this local adapter suite. Publishing a PR must first prove its workflows will not
compete with release CI or deploy; otherwise retain the local branch and prepared
draft PR description. Never merge/deploy as part of adapter validation.
