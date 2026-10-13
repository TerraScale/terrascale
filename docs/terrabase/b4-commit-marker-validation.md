# B4 own commit marker: local boundary validation

This patch implements and validates the engine marker boundary only. The client remote-ACK barrier, incarnation-scoped source/ACK integration and persistent receiver-copy recovery remain unimplemented. Design approval of 0013a at `40464691` is separate from implementation and runtime qualification; 0013b remains deferred.

## Committed result and client boundary

`CommitResult` retains the database offset and adds an optional `(database incarnation, own final CDC commit row)`. The exclusive writer reads that row after COMMIT and requires a CDC commit record. It releases the writer only after the result has been obtained. System work, uncaptured work and nonmutating requests receive no marker.

Group commit records each request's own captured-mutation bit after flushing its savepoint. Kept mutations share the committed batch's marker. An assertion-only request may share the offset but receives no sibling marker. Refused requests receive no committed result; their savepoints leave no document or CDC delta. A cancelled transaction returns no result and rolls back.

After a known successful COMMIT, a marker-read failure propagates once without automatic mutation replay and still notifies storage waiters. A later writer-session reset failure preserves the already obtained committed result and discards that session under B1's contract. The real HTTP controls require a marker-read failure to return 500 while the committed JSON remains present exactly once; a reset failure after marker acquisition preserves 201 Created. Following independent writes must succeed. The future structured `outcome_unknown` HTTP result and remote durability classification are not implemented here; current API answers remain local-only.

## Fixture ownership

R6 owns the new isolated `tests/TerraBase.Fast/Storage/CommitMarkerTests.cs`. It starts a real host with private resources, drives authenticated HTTP writes and reads, and compares native storage commit results against the real CDC source. It changes no shared test-harness implementation.

Two existing fixtures require mechanical adaptations to the changed engine return type: `tests/TerraBase.Fast/Management/UsageCommitHook.cs` changes `CommitAsync` to `ValueTask<CommitResult>` while preserving its pre/post commit gates; `tests/TerraBase.Fast/Storage/StorageMutationTests.cs` stores the unused commit task as `Task` rather than `Task<long>`, preserving every cancellation/assertion control. Accessible records identify no current owner for these mechanical decisions and no authenticated user reservation. The user's clarification explicitly permits routine scoped fixture work without a coordination-only COS wait. R3 source/ACK/harness ownership and all other product ownership remain intact.

## Serial evidence

The first marker build stopped on the return-type/style errors and ran no tests. After the mechanical fixes, fresh controls passed 6/6 with source/PDB identity 574/574. Explicit committed-JSON and cancelled-rollback assertions then passed 7/7 at `259e32c3`, with unchanged source and artifact manifests.

Mutation evidence at `3d14c41c` is retained under `/tmp/r6-q20-marker-mutants-qualified`. Each stage requires build exit 0, source/PDB identity 574/574 and unchanged DLL/PDB hashes before qualification:

| Mutation | Required failure |
| --- | --- |
| Read the previous CDC commit row before COMMIT | The marker differs from the mutation's own committed CDC group |
| Give every kept batch request the batch marker | The assertion-only request inherits a sibling marker |
| Suppress notification after a failed marker read | The successful COMMIT does not advance the waiter sequence |
| Return a zero incarnation | The marker does not identify the writer's actual incarnation |
| Swallow the marker checkpoint failure | HTTP returns Created when the control requires failure |

The initial pre-COMMIT mutant caused collection setup to fail before the marker assertion and received no qualification credit. Its complete evidence remains under `/tmp/r6-q20-marker-mutants`. The refined control allows an empty previous head and reaches the intended stale-marker assertion. All five mutations failed at the intended assertions and were restored. The restored fresh build exited 0, identity matched 574/574, and all 7 controls passed with source and artifacts unchanged. Required checks and exact publication-head evidence are recorded in the PR; review and CI remain separate gates.

## Concrete B4 dependencies

R3 owns `Engine/Sqlite/TursoChangeGroupSource.cs` and `Engine/Sqlite/SqliteStorageTransaction.Tail.cs`. The current `Features/Replication/ChangeGroups.cs` source contract returns bare offsets/groups and `AckAsync` accepts only `(database, peer, changeId)`. `Features/Replication/Protos/terrabase/v1/replication.proto` Database/Batch/Ack messages carry no incarnation. B4 requires incarnation-bearing Read/Head/Acked/Retains/Digest/Buckets, expected-incarnation refusal, and ACK persistence conditional on the incarnation while holding the database writer. A delayed old-incarnation ACK must never certify a replacement file. R6 can integrate the sender/barrier path once that existing-owner contract is implemented and validated; this marker patch does not change R3's files.

R7 owns fail-closed rebuild of faulted copies into new incarnations. R8 owns key-aware digests. R10a must agree the node-system-database fault-record storage. Persistent refusal on simultaneous links, controlled same-volume restart, and the failed-sync/process-crash-before-durable-fault-record window remain blocking implementation and acceptance gaps. A reopened faulted incarnation must never falsely qualify a remote ACK. The isolated marker controls do not qualify those recovery properties or six-direction remote durability.
