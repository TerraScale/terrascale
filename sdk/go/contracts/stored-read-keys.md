# Stored SQL keys and document identities

Producer authority: TerraScale/terrascale
`1d823d8534fc5035859a47e263691d0c7cb7b744`. Relevant Documents and Sql
endpoint/model sources are unchanged since the audit's `a3cbf8b1` baseline.
User decision, 2026-10-22: SQL/PostgreSQL primary keys are user-chosen;
document point IDs remain canonical ULIDs.

`src/TerraBase/Features/Documents/DocumentRoute.cs` defines two contracts:
`CheckDocumentId` requires ULIDs; `CheckStoredId` accepts 1–256 UTF-16 code
units without Unicode controls. No normalization or case conversion applies.
`docs/terrabase/sql/relational-sql.md` section 3 documents decimal integer,
text, and JSON-array composite primary keys.

Shared list/query, legacy SQL, change-feed, shape, and export results can carry
either document ULIDs or SQL keys. Export's `after_collection` and `after_id`
must appear together; `ExportDatabaseEndpoint.After` checks the latter with
`CheckStoredId`. Shape snapshot offsets carry strict unpadded base64url UTF-8
stored keys (`GetShapeEndpoint.ParseOffset`, `GetShapeManifestEndpoint`).
Ordering and checkpoint semantics are unchanged.

The SDK uses `entityid.StoredKey` only for these results/checkpoints. Collection
and platform point inputs, transaction operation IDs, write acknowledgements,
and point-read response IDs still require ULIDs. Project/database/catalog
entity references continue to use the separate `Check`/`checkID` validators.
Routes, authorization scopes, media types, retries, and tenant binding do not
change. SQL transport/session changes owned by Go PR 62 are excluded.

Deterministic fixtures cover integer/text/composite/punctuation/Unicode keys,
SSE, shape operations/page tokens, export records/resume, invalid lengths and
controls, and strict point/write/transaction behavior. Live qualification is
pending parent-owned ingress and credentials; no signup or email was attempted.
