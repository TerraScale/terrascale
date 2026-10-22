# Repair writer identity

`IChangeGroupSink.RepairAsync` requires the local incarnation whose digest selected
the repair. Every chunk reads the persisted incarnation after acquiring its durable
writer and joins only when it matches. A refusal returns `null`; earlier chunks
may already have committed to the named copy.

The engine transaction exposes the persisted identity through
`IStorageTransaction.GetIncarnationAsync`; decorators forward that read within
the same transaction. Callers must pass the compared incarnation through wrappers.

This changes the in-process interface. The public HTTP API, peer protocol, and
stored format are unchanged by the identity check.
