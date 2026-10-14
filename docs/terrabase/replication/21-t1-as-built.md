# 21. Slice plan: T1 as built

**T1 as built** (2026-10-12, storage format 3): the engine is Turso (`Turso.Data.Sqlite.Provider` 0.8.2; since vendored with fixes, `src/vendor/Turso.Data.Sqlite`). Each database
file has one writer connection (`SqliteDatabaseStore`, opened by the first writer under the writer gate, kept until the
store closes), with `PRAGMA capture_data_changes_conn('after')` on from its open. Continuous backup reads `turso_cdc`
(`backup.md`); Turso's quirks are in `turso-spike.md` ("Found while swapping the engine"). Notes for R3:

- **Table.** `turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time, change_txn_id, change_type, table_name,
  id, before, after, updates)`. `change_type`: 1 insert, 0 update, -1 delete, 2 commit. `id` is the rowid. In mode `after`
  `before` and `updates` are NULL, and a delete has no `after`. Schema changes appear as `sqlite_schema` rows (a drop
  carries only the `sqlite_schema` rowid). Changes to `turso_cdc` itself are not captured.
- **Reading.** Read on a `TursoConnection` of your own, as `TursoChangeTap` does, never on the writer. Every visible row
  is committed. Read up to the last COMMIT row, so a page never ends inside a transaction. Bound every statement with a
  `LIMIT`: the provider steps a statement to its end when its reader closes.
- **Decoding.** Two ways, both tested on 0.8.2. In SQL, `bin_record_json_object(table_columns_json_array(table_name),
  after)` must sit behind `CASE WHEN change_type IN (0, 1) THEN … END`: a COMMIT row (`table_name` NULL) makes
  `table_columns_json_array(NULL)` throw for the whole page, and it cannot format BLOB values (`_tb_state` and the
  collection tables have none). Or read `after` raw and decode it in C# (`SqliteRecord.Decode`, used by the backup tap; it
  handles every serial type, BLOBs included). For a rowid alias (`INTEGER PRIMARY KEY`) the record may hold NULL or the
  value; take the rowid from `id`.
- **Change-group bounds.** The R1/R2a 2 MiB per-group capture limit is gone: a group is a whole Turso transaction, as
  large as the batch that committed it (up to 256 writes of up to 1 MiB each, `MaxBatch`). Re-bound groups when reading:
  page by rows and bytes as the tap does (1024 rows, 4 MiB), and split a large transaction into several deltas batches
  for the stream.
- **Pairing.** A request writes the collection row once per operation and the document's `_tb_state` row once, at its
  end. Pair each `_tb_state` row with the last row of that document's collection table before it in the same
  transaction; an earlier image of the same document is superseded.
- **Toggle.** `SqliteStorageTransaction.SuspendCaptureAsync` turns capture off for the rest of a write transaction (call
  it before the first write: off mid-transaction still records the earlier rows and the COMMIT row); capture comes back
  when the transaction ends, and a failed reset closes the writer so the next one opens with capture on. Anything
  written uncaptured is missing from every backup, so only state that a restore rebuilds belongs there (`_tb_feed`,
  `_tb_merkle`, `_tb_tail`); R3 must rebuild it when a database is imported from a restore or a copy.
- **Truncation.** The backup tap deletes `turso_cdc` rows before its anchor on the writer (`TursoChangeTap.Truncate`),
  keeping the newest row. Once the tailer and peer streams read the table too, deletion must go below the smallest of
  all consumers' positions (the tap's anchor, `_tb_tail`, the peers' `acked`); move that into one place on the store.
- **Writes outside the writer.** None. A second connection that writes a file breaks Turso (the spike) and is invisible
  to change capture. Tests that need one hold the writer gate (`StorageFixture.ExecuteWhileWriterHeldAsync`).
