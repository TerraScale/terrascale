# SQL Server offline adapter

Run the synthetic example from the repository root:

```sh
nice -n 19 python3 -B tools/import-adapters/sqlserver/convert.py \
  tools/import-adapters/sqlserver/fixtures/capture/manifest.json \
  /tmp/sqlserver-import-example
nice -n 19 python3 -B tools/import-adapters/sqlserver/test.py
```

The output directory must be new and outside the capture directory. Python 3.11+
on a POSIX system with standard-library SQLite support is required; no packages,
SQL Server installation, license or account are required. Tests use only synthetic
captures and isolated temporary directories, with four concurrent CLI workers.
There is no equivalent Make target for this standalone adapter.

This converter accepts **`sqlserver-jsonl-v1`**, a documented capture interchange
format below, for SQL Server **2016–2022 (major versions 13–16)**. It does not open a
database, connect to SQL Server, execute SQL, load extensions, run shell commands,
upload anything or take credentials. `.bak`, MDF/LDF, SQL scripts, CSV, native BCP,
compressed archives and arbitrary JSON exports are unsupported. Native BCP needs
matching tooling, format files and encoding knowledge; renaming it does not make
it this format. Neither a live SQL Server export nor a TerraBase import was run.

## Output and compatibility

`import.sql` is UTF-8 PostgreSQL plain text with only quoted `CREATE TABLE`,
`NOT NULL`, `PRIMARY KEY`, and tab-delimited `COPY ... FROM stdin` sections. It is
an **offline artifact for core format `postgres_plain`**, not a new server API.
It follows the accessible contract [`docs/terrabase/import.md` at
54c0c4d9](https://github.com/TerraScale/terrascale/blob/54c0c4d9/docs/terrabase/import.md)
(v1 revision 3). Core implementation remains owned separately in draft PR #167.
Local checks independently decode generated COPY data; they do not certify the
unmerged core parser or its HTTP API. No integration edits are included.

`provenance.json` records source version/consistency, manifest SHA-256, every chunk's
hash/count, source names and type metadata, target types, collection names,
primary keys, output bytes/hash and explicit loss warnings. It contains no row
values. Treat it as private schema metadata. Keep it beside the artifact; only
`import.sql` is the core input. This converter does not perform the upload flow.

The same manifest and chunks produce byte-identical SQL and provenance. Manifest
table/column/chunk order and row order are significant. No conversion timestamp
or random ID is added. Input files are opened read-only, and symlink/non-regular
input files are refused. Source chunks are checked for size, count, SHA-256 and
modification during reading. Hashes bind the supplied capture, but cannot prove
that the exporter captured all source data or a consistent transaction snapshot.

On validation failure, no uploadable `import.sql` is exposed, and this invocation's
temporary output is removed. Existing output directories are never overwritten.
A process kill or machine crash can leave `.import.tmp` / `.keys.tmp`; these are
incomplete and must never be uploaded. Use a fresh output path for another run.
Directories are private (0700) and output files are 0600. Diagnostics contain only
fixed error codes and numeric locations, never values, paths or exception text.

## Capture layout

The [synthetic manifest](fixtures/capture/manifest.json) is a complete example.
Put it and all declared chunk files in a dedicated read-only capture directory.
Chunks use simple basename filenames ending in `.jsonl`; paths and symlinks are
refused. Each chunk has exact `bytes`, lowercase hex `sha256`, and `rows`. Empty
tables still declare one empty chunk (zero bytes/rows, SHA-256 of empty bytes).
Chunk boundaries must fall between records, never within a record.

Top-level keys are exactly `format`, `source`, `tables`. `source` contains exactly:

```json
{
  "product": "Microsoft SQL Server",
  "majorVersion": 16,
  "encoding": "UTF-8",
  "consistency": "snapshot_transaction"
}
```

Consistency is one of `snapshot_transaction`, `database_snapshot`, `quiesced_copy`.
These are required descriptions of the capture, not options that the converter
can turn on. It cannot verify the exporter's assertion.

Each table has exactly `schema`, `name`, `columns`, `primaryKey`, `chunks`. Each
column has exactly `name`, `type`, `nullable`, `identity`, `computed`, plus the
type-specific fields in the mapping below. The three booleans describe source
metadata. A nonempty, non-null primary key is required; this subset allows only
integer, `nchar`/`nvarchar`, and textual `uniqueidentifier` key columns. No guessed
rowid or generated key is substituted. Duplicate mapped IDs are refused across
all chunks of each table. A disk-backed key index uses only converter-created
SQLite tables and parameterized inserts; no supplied SQLite file or SQL is opened.

Each physical UTF-8 line is one positional JSON array, in declared column order,
ending in LF (CRLF also accepted). Every column has a slot, including computed and
identity columns. `null` is SQL NULL; a missing/extra slot fails. Strings `""`,
`"\\N"`, and `"\\."` remain strings. All non-null values are JSON strings except
`bit`, which uses JSON `true`/`false`. Numeric JSON values are refused rather than
guessed or rounded. Escape embedded newlines in JSON; no BOM, duplicate JSON object
keys, invalid UTF-8, lone surrogates, NUL, NaN/Infinity or exponent notation.

## Supported mappings

| SQL Server type | Extra manifest fields / accepted value | PostgreSQL artifact → core JSON |
| --- | --- | --- |
| `tinyint`, `smallint`, `int`, `bigint` | Canonical signed digits, no `+`, leading zeros or `-0`; source range checked | `smallint` / `smallint` / `integer` / `bigint` → exact integer number |
| `decimal`, `numeric` | `precision` 1–38, `scale` 0–precision; fixed-point string with exactly scale fractional digits | `numeric(p,s)` → exact string, including trailing zeros and negative zero |
| `money`, `smallmoney` | Exactly four fractional digits; signed 64-bit / 32-bit scaled-by-10000 range | `numeric(19,4)` / `numeric(10,4)` → exact string; no currency metadata |
| `datetime2`, `datetimeoffset` | `scale` 0–7; `YYYY-MM-DDTHH:mm:ss[.fraction]`; offset type requires `±HH:mm` | `text` → exact string; no temporal conversion or UTC normalization |
| `time` | `scale` 0–7; `HH:mm:ss[.fraction]` | `text` → exact string |
| `date` | `YYYY-MM-DD`, year 0001–9999 and valid calendar date | `date` → same string |
| `uniqueidentifier` | Text `8-4-4-4-12` hexadecimal UUID only | `uuid` → lowercase canonical string; native mixed-endian bytes refused |
| `nvarchar`, `nchar` | `length` 1–4000 UTF-16 code units; `nvarchar` also accepts `"max"`; `nchar` requires captured padding | `text` → unchanged Unicode string, including trailing spaces |
| `varbinary`, `binary` | `length` 1–8000 bytes; `varbinary` also accepts `"max"`; `0x` + even hex digits; fixed `binary` requires padding | `bytea` → standard base64 string; `0x` is empty binary |
| `bit` | JSON boolean only | `boolean` → JSON boolean |

Temporal fractions must have **exactly** the declared scale. No rounding, assumed
timezone, DST inference, leap seconds, hour 24 or offset-less datetimeoffset.
Offsets range from −14:00 to +14:00; both local and UTC dates must fit years
0001–9999. `-00:00` is refused as an unknown offset; use known `+00:00`. Seven-digit
100 ns precision remains in strings because the core timestamp mapping accepts
at most six fractional digits. Consumers must parse those strings explicitly if they need time
arithmetic. No fictitious zone name is reconstructed from a numeric offset.

Identity and computed column **captured values** are ordinary fields. Seeds,
increments, expressions and generated behavior are lost and disclosed. The
converter never evaluates an expression. Only approved materialized computed
values belong in a capture; do not use an exporter that invokes arbitrary UDFs,
CLR code, functions or views to produce them.

Everything else is refused, including `float`/`real`, legacy `datetime`/
`smalldatetime`, `varchar`/`char` (code-page interpretation), `text`/`ntext`/`image`,
`rowversion`/`timestamp`, `sql_variant`, XML, spatial, hierarchyid, CLR/user-defined
types and aliases. Resolve an explicitly approved extraction to supported base
types in a new capture rather than mislabeling the original type. This is not
universal SQL Server schema support.

## Names, IDs and fixed bounds

Schema/table names must be ASCII; quoted punctuation and mixed case are supported.
Column names may be Unicode. All identifiers must have 1–63 UTF-8 bytes and no
control characters. SQL Server's wider 128-character identifiers and non-ASCII
schema/table names are refused rather than silently renamed. The latter also
avoids differences in Unicode case conversion/UTF-16 collection sanitization.
Quotes are doubled; SQL-looking names are data, never injected statements.

Source schemas are retained: `dbo.orders` becomes collection `dbo_orders` under
the core rule; `public`/`main` drop their schema. The core lowercases, replaces
other characters with `_`, prefixes `t_` when needed and cuts to 63. Reserved
names or collisions are refused. Column names remain document field names.
One-column IDs are source key text (integer canonical digits, lowercase UUID,
or unchanged string). Composite components escape `%` as `%25` and `:` as `%3A`,
then join with `:`. IDs must have 1–256 UTF-16 code units and no control characters.
SQL Server collation, case-insensitive comparisons and trailing-space key
comparisons are not reproduced; the exporter must use the actual declared PK.

Fixed bounds: 1 MiB manifest; 1 GiB total manifest/chunk bytes and output SQL;
1000 tables/chunks total; 1600 columns per table; 16 MiB JSONL/COPY line; 1 MiB
interpreted SQL statement; 1 MiB mapped document. The adapter measures a
conservative ASCII-escaped JSON representation, so some Unicode-heavy records
that the core would accept are refused. Documents are flat scalar objects, within
the core 16-level/4096-value bounds. One bounded row is processed at a time; the
duplicate-key index has a 2 MiB SQLite page cache and a 1 GiB disk limit. Allow
space for inputs, up to 1 GiB output SQL and up to 1 GiB temporary key index.
Many tiny rows can exhaust the index before reaching the input/output limits;
conversion then fails with `key_index_capacity`. Split such captures into smaller
independent imports. Bounds are constants, not user tuning options.

The core also caps serialized previews at 512 KiB. A wide schema may convert
successfully here and still be rejected with `preview_too_large` by the core.
Artifact syntax validation is separate from core preview/quota acceptance; review
the core preview before any copy, including for successfully converted files.

## Read-only export procedure on the owner's machine

This procedure is for a source owner using existing approved tooling. Nothing in
this repository runs it. Do not give this converter credentials or proprietary
snapshots. Do not install SQL Server or acquire a license to run its tests.

1. **Choose an existing consistent source.** If `ALLOW_SNAPSHOT_ISOLATION` is
   already available, use one session with `SET TRANSACTION ISOLATION LEVEL
   SNAPSHOT; BEGIN TRANSACTION;`, export every table in that transaction, then
   `ROLLBACK TRANSACTION;`. Do not enable database options here. Otherwise use an
   existing approved read-only database snapshot or an approved quiesced copy.
   Do not use `NOLOCK`, dirty reads, independently captured tables or RCSI alone
   as a cross-table snapshot. Freeze schema changes for the export; a transaction
   snapshot alone does not guarantee consistent catalog metadata under DDL.
2. **Capture an explicit inventory.** Read the selected base-table metadata from
   `sys.schemas`, `sys.tables`, `sys.columns`, `sys.types`, `sys.indexes` and
   `sys.index_columns` in that consistent source. Record PK order, nullability,
   identity/computed markers, decimal precision/scale and temporal scale.
   `sys.columns.max_length` is bytes: divide it by two for `nchar`/`nvarchar`;
   `-1` means max. Refuse encrypted/unreadable/unsupported fields. Record the
   extraction scope privately; views, history tables, triggers and other objects
   are not automatically enumerated by this converter. Do not claim a full
   database export when the inventory selects only some tables.
3. **Stream typed rows through an existing trusted driver.** Select explicit
   base-table columns in manifest order, ordered by the complete PK. Read SQL
   decimals through an exact SQL decimal representation (`SqlDecimal`, not a
   float or .NET `decimal`, whose range is smaller than decimal(38,s)). Render
   fixed digits with invariant culture. Render datetime2/time from full 100 ns
   ticks, and datetimeoffset from full ticks plus its numeric offset, with exactly
   scale digits. Read UUIDs as text, Unicode without trimming/normalization,
   binary as bytes rendered hex, and bit as boolean. Serialize arrays using a
   real JSON encoder; keep NULL slots. Fetch large values completely and refuse
   client truncation. No arbitrary SQL from files, expressions, extensions,
   functions or shell/meta commands may be executed.
4. **Seal the capture.** Write UTF-8 without BOM, one array and LF per row. Split
   at row boundaries into basename `.jsonl` chunks. Close files before calculating
   bytes, rows and SHA-256; populate the manifest using those exact bytes. Verify
   each table's streamed count against `COUNT_BIG(*)` in the same transaction or
   static snapshot. Make the capture read-only and keep it unchanged. Rerun an
   incomplete extraction from a fresh consistent snapshot, never append a later
   snapshot's rows.
5. **Convert to a fresh local directory.** Run the CLI above, inspect provenance
   and loss warnings, and independently review the output fixture. Any future
   upload must use the core's documented preview/confirmation flow into a new
   empty destination; no upload, credentials, CI/deployment or live validation is
   authorized by this adapter's local conversion.

A trusted SELECT can project exact strings before an existing client JSON encoder.
For a known, reviewed base table, the following illustrates extraction only:

```sql
SELECT CONVERT(nvarchar(20), [id]) AS [id],
       CONVERT(nvarchar(50), [amount]) AS [amount],
       CONVERT(nvarchar(50), [wallet], 2) AS [wallet],
       CONVERT(nvarchar(36), [guid]) AS [guid],
       [occurred], [offset], [note], [payload], [active]
FROM [dbo].[orders]
ORDER BY [id];
```

Keep temporal and binary values typed until the client renders the exact format
above. Money conversion uses style 2 to keep four digits; the default string
conversion would retain only two. This result set is **not** the capture file: the client must serialize
positional arrays matching its manifest, without headings, result summaries or
SQL client truncation. `FOR JSON` defaults that omit NULLs, GUI CSV exports and
`sqlcmd` output defaults do not satisfy this contract.

## Verification

[Expected COPY output](fixtures/expected/import.sql) and
[expected provenance](fixtures/expected/provenance.json) are committed synthetic
fixtures. The parallel suite drives the real CLI and checks exact digits,
NULL/empty/missing distinctions, escaping, binary, Unicode, time/offset ranges,
identifiers, keys across chunks, large rows, malformed input, source immutability
and failure cleanup. No external tests or service suites are run. Local publication
notes are kept separately from the adapter's reusable documentation.
