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
- **Tenants.** Database-per-tenant is first-class: `ts.tenants.create(key)`
  mints a database from a tenant template (D-51).
- **Placement.** A database's bytes at rest exist only in its selected Regions;
  the placement lock cannot be widened (D-52).

See [Runnable snippets](./snippets.md) for executable versions of each idea.
