# MongoDB offline export adapter

Run from the repository root with **Python 3.11+ on Linux**, using only the standard library:

```sh
nice -n 19 python3 -I -B tools/import-adapters/mongodb/convert.py \
  tools/import-adapters/mongodb/fixtures/canonical.jsonl \
  tools/import-adapters/mongodb/example.sql \
  --database synthetic --collection records
nice -n 19 python3 -I -B tools/import-adapters/mongodb/test_cli.py
```

The first command creates a **new** local file; a repeated command refuses to overwrite it.
Success reports the artifact's byte count and SHA-256, for the documented core `sourceBytes` and
`sourceSha256` fields in a future import. These describe the output artifact; the original JSONL hash
is separately retained in each record's provenance.
`fixtures/canonical.jsonl` and `fixtures/expected.sql` contain only hand-written synthetic data.
The adapter has no network client, credentials, upload path, server dependency, or package installation.
Database and collection arguments are nonsecret source labels, used verbatim in provenance and ID derivation.

## Supported source subset

One collection per file/artifact: **mongoexport JSON Lines, canonical Extended JSON v2**, the format exposed
by MongoDB Database Tools 100.x with `--type=json --jsonFormat=canonical`. This is a format compatibility
claim, not certification of every MongoDB server/tool release. Tests use synthetic format fixtures; no
MongoDB server or mongoexport executable was run. Record the exact Tools and server versions with your
export. BSON dumps from mongodump, legacy Extended JSON v1, CSV, JSON arrays (`--jsonArray`), multiline
pretty JSON, compressed files, and relaxed/raw numeric values are unsupported.

| Source value | Exact mapped representation inside `document` |
| --- | --- |
| String, boolean, null, object, array | JSON value; nested arrays/objects remain nested; missing members remain absent |
| ObjectId | `{"$oid":"24 lowercase hex digits"}` |
| Int32 / int64 | `{"$numberInt":"digits"}` / `{"$numberLong":"digits"}`; signed BSON range checked |
| Decimal128 | `{"$numberDecimal":"original spelling"}`; IEEE decimal128 exact representability checked without rounding |
| Double | `{"$numberDouble":"original spelling"}`; binary64 range checked; signed zero and subnormals retained |
| Date | `{"$date":{"$numberLong":"UTC epoch milliseconds"}}`; full signed int64 range |
| Binary | `{"$binary":{"base64":"padded standard base64","subType":"two lowercase hex digits"}}`; bytes and subtype retained |
| BSON timestamp | `{"$timestamp":{"t":uint32,"i":uint32}}`; timestamp and increment retained separately |

Numeric wrappers preserve strings, including Decimal128 scale/exponents and double spellings.
`NaN`, `Infinity`, and `-Infinity` are accepted **inside decimal/double wrappers only**, as strings.
Decimal validation uses precision 34, exponent range −6143 to +6144 with IEEE clamping, and traps inexact
conversion and overflow. Exact trailing-zero adjustments and zero-exponent clamping are accepted because
they do not change the numeric value; the original wrapper string is retained. Signaling NaNs and other
special spellings are rejected. Double strings must describe a finite binary64 value, or one of the three
explicit specials; overflow and nonzero values that underflow to zero are rejected.

Only BSON timestamp's `t` and `i` accept raw JSON numbers; they must be uint32 integer tokens. Every other
numeric BSON value requires its canonical wrapper. Every object containing a dollar-prefixed key must be
exactly one supported wrapper. Ambiguous user dollar fields and unsupported wrappers fail the whole
conversion. JavaScript/code/scope, regex, DBPointer, symbol, undefined, min/max key, and `$uuid` convenience
syntax are rejected. UUID bytes exported as `$binary` subtype `04` remain supported without reinterpretation.
Uppercase ObjectId/subtype spellings, integer leading zeros/negative zero, noncanonical base64, duplicate
JSON members, malformed UTF-8, BOMs, NUL characters, and lone surrogate escapes are rejected.
LF, CRLF, and an unterminated final record are supported; blank lines are rejected.

## Output and IDs

The artifact is **`postgres_plain`**, as specified by
[`docs/terrabase/import.md`](../../../docs/terrabase/import.md), contract v1 revision 3 at `54c0c4d9`.
That contract is on core draft [PR #167](https://github.com/TerraScale/terrascale/pull/167), so the relative
link is absent on this adapter's base branch until the core lands. The adapter introduces no server format
or API. It writes fixed UTF-8 settings, one fixed table, and PostgreSQL text COPY data:

```sql
CREATE TABLE public.mongodb_documents (
    id text NOT NULL PRIMARY KEY,
    document jsonb NOT NULL,
    provenance jsonb NOT NULL
);
COPY public.mongodb_documents (id, document, provenance) FROM stdin;
```

The core's default primary-key mapping gives collection **`mongodb_documents`**, using the text `id` as
the document ID. The exact target body is:

```json
{
  "id": "0000000000…16 Crockford base32 characters…",
  "document": { "_id": { "$oid": "0123456789abcdef01234567" }, "nullable": null },
  "provenance": {
    "adapter": "mongodb-ejson-v1",
    "sourceFormat": "mongoexport-canonical-ejson-v2-jsonl",
    "sourceSha256": "SHA-256 of the complete original JSONL bytes",
    "database": "synthetic",
    "collection": "records",
    "sourceRecord": 1
  }
}
```

`document` retains the original `_id`; nothing is flattened or inferred into relational columns. Wrapper
objects are an explicit BSON representation, not native TerraBase numeric/date/binary values. For example,
an int64 is queried through `document.long.$numberLong` as a string; MongoDB query semantics do not transfer.
COPY escapes backslashes, tabs, CR, and LF independently of JSON escaping. Literal `\N` inside a string
never becomes SQL NULL, and embedded SQL or shell/meta-command text remains inert string data.

**ID derivation, version 1:**

1. Form a typed key array: `["objectId", hex]`, `["string", string]`, or `["integer", canonical signed digits]`.
   Int32 and int64 share the integer domain, so equal cross-type numeric `_id` values are duplicates.
2. Serialize `["mongodb-ejson-v1", database, collection, typedKey]` using Python JSON with `ensure_ascii=True`,
   compact separators `(',', ':')`, and no key sorting. SHA-256 these ASCII bytes.
3. Build a ULID with six zero timestamp bytes and the first ten digest bytes as its 80-bit payload. Encode
   the 128 bits into 26 uppercase Crockford base32 characters. Zero time is a sentinel, not an export date.

IDs depend on namespace and typed `_id`, not file path, clock, source hash, document content, or row order.
ObjectId and its hex-shaped string are distinct; integer `1` and string `"1"` are distinct. Empty strings
and control characters in string `_id` values are representable through the generated ULID, provided no
NUL or invalid Unicode is present. Missing `_id`, null, booleans, arrays, embedded-document IDs, decimal/double
IDs, dates, and binary IDs are rejected. Decimal128/double document fields are supported; those **ID types**
are excluded to avoid ambiguous cross-type numeric equality rules. Duplicate typed source keys and
generated ULID collisions are rejected by an on-disk unique index before publication. This is not a
global collision guarantee; the core also validates its create-only writes.

Byte-identical input and identical namespace labels produce byte-identical output. Row and object-member
order are preserved in the artifact, with compact JSON and ASCII Unicode escapes. Reordering input rows
changes the artifact/provenance ordinals while leaving each record's derived ID stable.

## Loss warnings and bounds

**This adapter imports document values only.** MongoDB indexes (including uniqueness), validators,
collations, TTL behavior, users/roles, views, collection options, sharding metadata, transactions, change
streams, query operators, and MongoDB equality/order semantics are not imported. BSON dates contain UTC
milliseconds only: neither an original offset nor a named timezone exists to recover. Keep any application
timezone fields as separate strings. Relaxed ISO date strings, including offset-bearing dates, are rejected.
The artifact preserves object-member order, but target JSON storage/query behavior does not promise MongoDB
field-order semantics. Numeric wrapper strings retain precision but require explicit application handling.

The **full target body**, including provenance and wrapper overhead, must fit **1 MiB, 16 container levels,
and 4096 values**, matching the documented core admission rules. Each scalar, object, or array counts as one
value, starting with the body at depth 1. JSON property names do not count as values. The adapter's byte
check uses its ASCII-escaped serialization and is conservative for Unicode. A MongoDB-valid 16 MiB BSON
document can therefore be rejected; large records are never truncated or split into different documents.
Source files and output artifacts are each capped at 1 GiB; source JSONL records at 16 MiB including framing.
Numeric wrapper strings are capped at 128 characters (integer wrappers at 20), and namespace labels at
255 UTF-8 bytes without ASCII controls. These limits are constants, not tuning flags.

Input is opened read-only as a regular, nonsymlink file. The adapter hashes the source first, then streams
one bounded record at a time while rehashing it; changed hashes or file metadata abort publication.
This catches changes during conversion, not an export that was already inconsistent. It does not create
a snapshot of a mutable source. Filesystem reads can update access time; input contents and permissions
are never changed by the adapter.

A private temporary directory beside the output holds the candidate artifact and SQLite duplicate index.
SQLite executes only adapter-owned fixed statements on that **new scratch index**, never export SQL or a
source database. Its cache is fixed at 2 MiB; index storage grows with the typed IDs. Reserve disk space for
both the artifact and index, potentially more than the source size. Normal failures remove temporary files
and publish no output; an abrupt process kill can leave the private scratch directory. Successful publication
uses an atomic no-clobber hard link on the same filesystem, with file mode `0600`; filesystems without hard
links fail closed. Existing files, symlinks, or the source itself are never overwritten. Diagnostics name
record positions and fixed failure reasons, never document values, IDs, namespace labels, or input paths.

## Consistent export instructions

These are operator instructions; this adapter never connects to MongoDB or runs mongoexport.

1. Choose an already consistent backup/snapshot and restore it to an isolated local MongoDB instance using
   the backup's documented recovery procedure. Keep the original snapshot read-only and unchanged. Alternatively,
   arrange a maintenance window that stops **all** writers, migrations, background TTL deletions, and other
   mutations for the full export interval. Read permission alone does not freeze a collection.
2. From that immutable/restored or fully quiesced instance, use a read-only account and export the complete
   collection with Database Tools 100.x. For a credential-free local synthetic instance, the command shape is:

   ```sh
   mongoexport --host=127.0.0.1 --port=27017 --db=synthetic --collection=records \
     --type=json --jsonFormat=canonical --out=records.canonical.jsonl
   ```

   Use your existing local credential handling if authentication is required; never put credentials in the
   adapter labels, artifacts' filenames, shared commands, or logs. Do not use projection/filter/limit,
   `--fields`, `--query`, `--queryFile`, `--skip`, `--limit`, `--jsonArray`, or `--pretty` for a complete export.
3. Record source/tool versions, collection count, export interval and consistency procedure locally. Verify
   the export count against that same immutable source, freeze the completed JSONL, and convert it to a fresh
   output path. Compare the reported converted count with the expected source count before any future import.

**mongoexport is not a point-in-time backup and does not establish a snapshot across a changing collection
or across collections.** Majority read concern is not a transaction snapshot and does not fix this limit.
An export can miss, repeat, or mix records while writes continue. The adapter catches duplicates but cannot
detect omitted records, filtered exports, a stale backup, or mixed versions of distinct records. A checksum
proves which file was converted, not that the file was complete or consistent.

The current CLI intentionally handles **one collection per import**. The documented core provisions one new,
empty destination database per import. This adapter does not combine separate exports or provide a multi-collection
migration. There are no core, SDK, or dashboard edits. Future core use still requires its confirmed preview;
this work performs no uploads, core integration, merge, deployment, or live/customer tests.

## Validation and publication

`test_cli.py` runs independent synthetic cases concurrently, each in its own temporary directory under this
adapter directory. It drives the real CLI, checks committed golden bytes, and independently unescapes text
COPY and decodes the resulting JSON bodies. Precision/range, null/missing, subtype/encoding, timestamps,
identifiers, source immutability/change detection, large/chunked records, duplicate IDs, malformed records,
body limits, concurrent no-clobber publication, nonregular input, and diagnostic redaction are covered.
This is independent validation of the **documented** format;
the actual core reader and PostgreSQL are not run, so end-to-end core acceptance remains unverified.

CI preflight at base `e30ffdc2`: `tools/**` matches no area filter, but `ci.yml` runs unconditional `changes`
and `test` jobs for draft PRs, both using `${{ vars.CI_RUNNER || 'ubuntu-latest' }}`. Read-only preflight with
`gh variable get CI_RUNNER -R TerraScale/terrascale` returned `alpha` on 2026-10-12: these jobs share Alpha's
release runner pool. Preview/deploy jobs are currently disabled, which does not remove that contention risk.
**Do not publish this branch/PR under the current preflight.** A prepared local draft PR body is in
[`PR_BODY.md`](PR_BODY.md). No workflows, root build files, or global configuration are changed. Repository-wide
`make check`, security/analyzer/Fast gates, real exports, and core integration were not run; this isolated
offline Python tool has only local synthetic validation and no adapter CI wiring.
