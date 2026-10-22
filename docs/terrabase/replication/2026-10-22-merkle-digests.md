# Maintained repair digests

`_tb_merkle` stores each bucket's XOR in the same writer transaction as state
writes and deletes, including GC, collection purges and rebuild writes.
Digest reads inspect at most 4096 summary rows; bucket pages still validate each
stored keyed state. Direct-state test fixtures maintain the same summaries.

The digest scan checks bucket ranges and scalar types. Integer-valued corruption
of a cached summary requires rebuilding the copy; this scan is not a physical
integrity audit.

Needs format bump: the system schema gains `_tb_merkle`, and existing client
summaries must be rebuilt by the coordinated format reset. `StorageFormat.cs` is
unchanged. No in-place migration is provided. The peer protocol is unchanged.
