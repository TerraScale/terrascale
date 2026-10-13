Title: feat(import): add isolated MySQL/MariaDB offline COPY adapter
Base: main
State: draft

Convert a restricted MySQL 8.0/8.4 or MariaDB 10.11/11.4 dump profile locally into
the import core's existing PostgreSQL plain COPY artifact. The CLI parses complete
literal INSERT rows and never executes export SQL. Dry-run checks every row and
writes a conversion/provenance report; conversion adds the artifact in a new directory.

Unsigned BIGINT and DECIMAL preserve exact digits as JSON strings; binary becomes
bytea/base64, NULL stays distinct from empty/missing, and TIMESTAMP requires explicit
UTC. Original tables, columns and key metadata are retained. Unknown syntax/types,
zero dates, expressions, missing columns, duplicate document IDs and ambiguous
SQL mode/charset reject the whole conversion. The README documents the precise
subset, exporter-version declaration limits, snapshot instructions and semantic losses.

Validation: low-priority parallel synthetic CLI checks with an independent COPY
decoder, golden output, byte-identical reruns, read-only source checks, malformed
inputs, precision/NULL/binary/timezone/identifier boundaries, chunked Unicode records
and extended INSERT statements over 1 MiB. Exact final check count is recorded with
the completion report; no service/core integration or repository-wide .NET checks ran.

Compatibility/storage/security: only tools/import-adapters/mysql changes. No core
format/API, SDK, authentication, dashboard, replication, root configuration or workflow
changes. Source files open read-only; output destinations are never overwritten.
Local reports/artifacts contain source data/schema and must remain private. No
live connections, credentials, customer data, uploads, external tests or dependencies.
Constraints/collations/defaults are not enforced in TerraBase; warnings are explicit.
Core validation remains mandatory. No migrations, merge or deployment.

Publication preflight: local main e30ffdc287530551b161b0130220be53d3c0475e defines
unconditional PR changes/test jobs using vars.CI_RUNNER; the live repository variable
is alpha. Opening this draft would compete with release CI. Keep this branch/commit
local; do not push/open the PR until a future approved preflight proves isolation.
