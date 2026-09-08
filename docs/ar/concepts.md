---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

# Concepts

- **Documents and collections.** JSON documents with string IDs; schema JSON is
  the source of truth (`terrascale push` applies it, codegen reads it).
- **SQL is the only query language (D-78).** Read-only `SELECT` over extracted
  columns, `body` JSON paths, and `_` metadata columns. Writes stay document
  operations (create/update/patch/delete), never SQL DML.
- **Live queries vs collection sync (D-38).** A live subset `SELECT` (one
  collection, `WHERE` + `ORDER BY` + `LIMIT` + `MATCH`) is subscribable and
  maintained incrementally; collection sync keeps an authorized outer subset
  that a local store (e.g. TanStack DB) narrows client-side.
- **Branches.** Database branching incl. `terrascale dev` cloud dev branches —
  no customer-run emulator or local database process.
- **Tenants.** Database-per-tenant is the intended model. Tenant provisioning
  is awaiting its producer contract, so no public tenant provisioning API is
  available yet.
- **Placement.** A database's bytes at rest exist only in its selected Regions;
  the placement lock cannot be widened (D-52).

See [Runnable snippets](./snippets.md) for executable versions of each idea.
