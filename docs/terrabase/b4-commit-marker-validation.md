# B4 commit marker: unqualified source candidate

This worktree contains the engine marker boundary only. It has not been built, tested, reviewed or published. The client remote-ack barrier, incarnation-scoped source/ack integration and persistent receiver-copy recovery are not implemented by this patch. Design approval is separate from runtime qualification.

The engine commit result keeps the existing offset and carries `(database incarnation, own final CDC commit row)`, read on the exclusive writer after COMMIT. Group commit keeps a request's own mutation bit after its savepoint is flushed. Kept mutations share the committed batch marker; nonmutating and refused work receive no marker. Marker-read failure fails the batch without automatic replay while still notifying storage waiters of the successful COMMIT. A later session-reset failure keeps the already returned marker under B1's behavior.

## Next serial validation window

The existing #168 reviewer released its bounded heavy window after a fresh build and 30/30 focused controls at `1cfa2ad6`. R6 validated and published the bounded refill revision `5703b0c4`; its review and CI remain required. The serial queue now validates the reconciled B1 stack. This marker worktree must wait until that run finishes. The shared fixture's one return signature was adapted after the authenticated user's clarification that a coordination-only COS wait need not stop routine scoped test work; no separate decision was reserved and both gates are unchanged.

1. Build successfully with the private SDK and verify source/PDB plus DLL identity before any test credit.
2. Integration test: compare a committed mutation's marker with its CDC group's commit row, make an unrelated later write, and verify the original marker does not borrow the new head. Use the real host and HTTP reads to verify materialized documents.
3. Deterministically queue a batch behind a held writer: two kept mutations, assertion-only work and a savepoint-refused mutation. Both kept mutations get the same marker; assertion-only and refused work get none, and the refused document is absent. A whole-transaction rollback has no result or marker.
4. Inject `writer.marker` failure after COMMIT. The attempt fails once, storage waiters wake, the document is present exactly once in the source and no ambiguous mutation is replayed. Separately inject `writer.reset` failure after a marker was read; the returned marker and incarnation survive while the session is discarded.
5. Mutants: read before COMMIT; assign the shared marker to all kept work; and suppress the commit notification after a failed marker read. Require intended assertion failures, restore each mutation, then run required checks before publication.

The isolated marker fixture also drives real HTTP writes. A failed marker read after COMMIT must return a failure rather than client success while the committed document remains present exactly once and the incarnation stays unchanged. A failed session reset after a successful marker read must retain the successful client response. Both cases require a following independent write to succeed. These controls do not certify remote ACKs or the future `outcome_unknown` HTTP representation; that boundary is still unimplemented.

## Existing ownership and remaining dependencies

R6 owns this engine/document boundary and the isolated `tests/TerraBase.Fast/Storage/CommitMarkerTests.cs` fixture. `tests/TerraBase.Fast/Management/UsageCommitHook.cs` has only its `CommitAsync` return signature changed to `ValueTask<CommitResult>` so the existing adapter compiles; both interception gates and all assertions are unchanged. No actual current fixture owner was identified from accessible context, and no authenticated user specifically reserved this mechanical decision. R3's source and ACK files and all other owners' files remain untouched.

R3 owns incarnation-bearing reads and conditional incarnation-scoped ACK persistence. R7 owns rebuilding faulted copies into new incarnations. R8 owns key-aware digests. R10a must agree the node-system-database fault-record storage. Persistent refusal on simultaneous links, controlled restart, and the failed-sync/crash-before-fault-record window remain blocking implementation and acceptance gaps. The single-attempt outcome_unknown contract and deferred 0013b are unchanged.
