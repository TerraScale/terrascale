# Shape chunk sealer

`IShapeSealer` (`Features/Shapes/ShapeSealer.cs`, `Engine/Turso/TursoStorageTransaction.Shapes.cs`) seals a table's
captured changes into immutable bodies in `_tb_shape_chunk`. The protocol is [shape-reads.md](shape-reads.md).

The caller supplies a `ShapeSealDefinition`: shape id, handle, table, generation and incarnation. In its write
transaction, with capture suspended, the sealer first checks that the table's `sqlite_schema` SQL and recorded
generation still match, so a stale handle never seals.

1. `SealUpToHeadAsync(store, shape, offset, cancellationToken)` seals whole commits from `offset` through the head it
   observed on entry, one write transaction per chunk, and returns the sealed boundary.
2. `ReadChunkAsync(store, handle, offset, cancellationToken)` returns persisted bytes only, or null. The returned
   `ShapeSealedChunk` owns its memory admission.
3. `RunDatabaseAsync` runs one database's background worker for the tracked shapes; live flights seal on wake.

A chunk covers `(offset, end_offset]` of `turso_cdc.change_id`. It reads changes in id order: a commit record
(`change_type = 2`) closes a commit; row changes of the table become `insert`, `update` or `delete` ops decoded from
the captured after-image, with every stored column; changes of other tables only advance the range. Limits: 4096
row operations, 1 MiB of body, 65536 scanned changes, or one commit of up to 8 MiB (`ShapeTooLargeException`,
`shape_too_large`).

A captured `sqlite_schema` change whose `tbl_name` is the table, or an image whose column count differs from the
current definition, ends the lineage: the chunk closes before that commit, and a chunk that would start with it
retires the generation instead, so the next read answers `409 must_refetch` with the new handle. An offset that is
not a retained commit record raises `ShapeSealOffsetException` (`must_refetch`).

Sealed bodies are inserted once with `SuspendCaptureAsync` and never rebuilt. Shape tables are derived data: copies,
backups and rewrites omit their rows, and every shape restarts under a new incarnation. Memory is reserved before
decoding images and before returning bodies; refusal, rollback and cancellation release it.
