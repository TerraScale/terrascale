# Maintained collection counts

Each client database keeps one `_tb_collection_stats` row per collection: its live document count and the UTF-8 bytes
of their JSON. The row changes in the same statement group as the document row (`PutRow` in
`src/TerraBase/Engine/Turso/TursoStorageTransaction.States.cs`), so it commits and rolls back with the write. That
covers local writes, replicated joins, GC, hidden uniqueness losers and collection drops (`DropTable` deletes the row).

The following read these rows instead of scanning collection tables, so their cost depends on the number of collections,
not on the number of documents:

- `documents`, `logicalBytes` and `collections` in `GET D/usage` and `GET P/usage`;
- the schema collection list (`GET …/databases/{database}/collections`, its `documents` and `bytes`);
- the live-document storage quota (`GetStoredDocumentBytesAsync`), plus hidden uniqueness losers as before.

Before this change these reads ran a `count(*)`/`sum(octet_length(doc))` scan under the tenant query budget. On Hobby
that budget is 10,000 candidate rows, so once a database held about 10k documents, usage answered `400 query too
expensive`.

The table is part of the client schema digest, so adding it changed the storage format id (one data reset). Its rows
are written into the change log like any other table, so backups and restores carry them. Replication ships no stats
rows: each replica keeps its own counts from the documents it materializes.
