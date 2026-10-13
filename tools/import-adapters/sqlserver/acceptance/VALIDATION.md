# Preparation evidence — 2026-10-12

**Real SQL Server export and TerraBase acceptance: UNRUN.** Synthetic golden files
are expected outputs only; they are not evidence of SQL Server or core behavior.

## Local checks actually run

`nice -n 19 python3 -I -B tools/import-adapters/sqlserver/acceptance/test.py`

Result after review hardening: **66/66 parallel offline acceptance-preparation
CLI checks passed**. Initial commit `34d3052d` retains its original 64/64 evidence.
Each case owns a temporary directory and invokes the real checker CLI. Positive
sealing checks invoke the unchanged converter and compare all capture/output
files with the committed golden bytes. Checks cover precision, temporal offsets,
NULL/missing/empty, binary, Unicode and large rows, IDs, truncated/wrapped/malformed
input, file/row bounds, symlink/existing-output refusal and malformed/mismatched
synthetic preview, validation and document receipts. No SQL, HTTP, server or core
build runs in this suite. Added checks verify isolated Python startup ignores
synthetic `PYTHONPATH` injections for both CLIs and that quoted preview column
names fail closed against the currently inspected core's raw names.

Python AST syntax checks passed for both new Python files. `bash -n` passed for
all seven README Bash/shell blocks individually and as a combined session;
commands were parsed only. There is no available SQL Server runtime/parser, so
SQL syntax, catalog metadata, temporal rendering and sqlcmd output remain
unverified until the approved disposable run.

## Reproducible synthetic artifacts

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `golden-capture/rows-0001.jsonl` | 829 | `980255e6d9bbe991162375fb9e2aa59304b87e2c8677cb87e3bd74b6bf57f3ad` |
| `golden-capture/rows-0002.jsonl` | 49372 | `556dc4f91bbc3f0ff9825f589b8f10c2307a0ee7bfd25e028413989ed8a69f47` |
| `golden-output/import.sql` | 50898 | `41a5dcd1fb5ca9f441277181fb64801b616056dd831852577a34f3305fcee2d2` |

The manifest declares exactly **17 columns, four rows, two chunks (3 + 1)**;
the COPY artifact declares one primary-keyed table and expected collection
`dbo_sqlserver_acceptance`. Decimal/money and temporal fields preserve exact
strings; integers and bits remain JSON numbers/booleans, binary maps to base64,
and explicit NULL fields remain present. Identity/computed generation and source
collation/constraints are not recreated. Source and core row counts must both
be four, and all bodies must equal `expected-documents.json` by JSON type/value.

## Preserved work and prerequisites

Original commit `18d7877e` remains an ancestor; its tracked adapter files are
unchanged. Its existing **74/74** adapter test evidence is preserved, not rerun
or combined with these new preparation checks. The ignored local draft body is
unchanged:
SHA-256 `563dd69cad6b6568033b5640b1d2cf0b0bb5a58250c2ef1f75f5cf712fb8a1d5`.

Preflight found no sqlcmd/bcp/Docker/PowerShell or disposable SQL Server image.
Found TerraBase binaries lack import sources in their checkouts and are not
ready targets; integration approval is pending. Active Fast/compiler/dashboard
work ruled out competing builds. Follow the exact prerequisite list and clearly
unrun commands in `README.md`; do not fill missing prerequisites with installs,
license/account actions, source credentials, core builds or shared CI. This
preparation is local only, with no push, PR publication, merge or deployment.

## Independent read-only review

Review round 4 found no blocking preparation-code defect in the inspected SQL,
sqlcmd flags, artifact/manifest alignment, HTTP flow or fail-closed checks. It did
not execute SQL/HTTP or verify real core behavior, and did not inspect every test
or converter value-mapping branch.

Follow-up changes add isolated Python invocations and a held cleanup table lock
before the ownership-marker check, with rollback on refusal. Cleanup still
requires frozen fixture writers/DDL and separate explicit owner approval; the
script remains unrun. Original fixture, canonical capture and COPY bytes are
unchanged.

An external readiness discrepancy remains: contract `54c0c4d9` §6 quotes
non-bare preview column names, while `ImportPlanner` at core `70ec9570` returns
raw `note.名`. The checker follows inspected core source and refuses changed
names; the README requires resolution during a future approved readiness
preflight before real acceptance. No contract/core edits or owner messaging
were performed.
