Draft title: feat(import): add offline MongoDB canonical Extended JSON adapter

Base: main
Head: feat/import-adapter-mongodb
Status: prepared locally; publication blocked by CI preflight

MongoDB canonical Extended JSON v2 JSONL can now be converted offline into the existing `postgres_plain`
import artifact, without source connections or executing export contents. The local standard-library Python
CLI produces `public.mongodb_documents` with deterministic ULID primary keys, original typed documents and
source-hash/namespace/record provenance. It validates numeric precision, binary subtypes, UTC millisecond dates,
BSON timestamps, nested values, null versus missing, duplicate keys/IDs and the documented core limits.

Scope is exclusively `tools/import-adapters/mongodb`. It accepts one collection per artifact and `_id` types
ObjectId/string/int32/int64. Unsupported or ambiguous values fail the entire conversion; BSON type wrappers
are retained explicitly, never flattened or inferred as native target types. MongoDB indexes, validators,
collations, timezone origins, and query/equality semantics do not transfer. The README documents the source
subset, deterministic mapping, snapshot/export limitations and read-only local workflow.

Validation performed:

- `nice -n 19 python3 -I -B tools/import-adapters/mongodb/test_cli.py` — 76/76 parallel synthetic CLI tests passed.
- Synthetic output fixture: `tools/import-adapters/mongodb/fixtures/expected.sql`, three documents, independently
  decoded as PostgreSQL text COPY and compared with the original Extended JSON fixture.
- Contract v1 revision 3 read at `54c0c4d9:docs/terrabase/import.md`; no core format/API changes or integration edits.

Security/storage/compatibility: input is read-only, nonsymlink and hash-verified across two passes; artifacts
are atomically published without clobbering existing files, with mode 0600. A private scratch SQLite index
executes only fixed local statements for duplicate detection. No source SQL, functions, extensions or shell
commands execute. No network, credentials, uploads, customer data, paid services or proprietary dependencies.
Source values and labels are excluded from diagnostics. No production storage or auth/replication changes.

Publication blocker: existing PR workflows run unconditional `changes` and `test` jobs on the configurable
Alpha runner pool. `tools/**` avoids area builds, but cannot prove no competition with release CI. Keep this
draft local. Repository-wide `make check`, analyzer/security/OpenAPI/Fast gates, real mongoexport, PostgreSQL
and end-to-end core acceptance were not run; no adapter tests are wired into CI. No PR was created or pushed,
no review/auto-merge requested, and nothing was merged or deployed. Re-run preflight before future publication.
