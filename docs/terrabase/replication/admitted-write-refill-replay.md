# Live replay of admitted writes after a cut-log refill

This supersedes the cut-log resume rule in [Live acknowledgements after a database copy or restore](../restored-copy-live-acks.md). A refill ACK still certifies no write. Only a subsequent live batch ACK can confirm remote durability, within the existing ten-second wait.

After an unclean restart, a peer can have a new replica identity and a zero cursor while the sender has already trimmed its earliest change groups. Resuming that refill at the current head skips a write that committed before the head was read: its state reaches the peer through the refill, but no live batch can acknowledge it. The client eventually receives `write_outcome_unknown`.

The sender reads the source head before inspecting admitted attempts under the acknowledgement waiter's lock. With known unconfirmed markers, it requests the complete CDC transaction boundary immediately before the oldest marker of the current incarnation, bounded above by that captured head. The storage read clamps this boundary to the oldest complete retained transaction boundary. Older retained history is repaired by the refill without being replayed live. The marker minus one is a query bound, never a resume position inside a transaction.

If an admitted attempt has not registered its commit marker yet, its position is unknown and the sender conservatively requests the retained suffix. This fallback is limited to 64 MiB of captured after-images; above that fixed bound it resumes at the head, preserving the honest unknown outcome. The known-marker path has no byte cap: skipping a pending marker under load would recreate the confirmation hole. With no admitted attempts, it can use the captured head: any later admission commits strictly after that head. A confirmed attempt does not extend replay. A replaced or unavailable copy refuses the pinned boundary read and follows the existing hold/reset path.

A marker already removed by retention remains unconfirmed. Clamping past it cannot fabricate a live acknowledgement, and the write still answers `write_outcome_unknown`. The existing read-then-retains check detects a further cut before shipment. No storage schema, peer message or public API changes are required.

Fast integration coverage uses real listeners, production CDC and production retention to distinguish a bounded known-marker replay, a commit awaiting waiter registration, and a marker removed before refill. The negative case requires the document to arrive through the refill while its HTTP write still answers unknown.
