# Live acknowledgements after a database copy or restore

A copied database carries peer rows scoped to its previous incarnation. TerraBase returns those peers a zero cursor marked `RequiresRefill`, which stays required until a successful ACK records the new incarnation.

If the new incarnation retains its whole change log, refill names that zero resume position. The refill joins the current state, which can already contain writes committed after the copy. Those writes also ship explicitly in live batches after zero; the receiver durably applies each batch before acknowledging it. Only those live acknowledgements can confirm their remote durability. This prevents an immediate post-restore write from disappearing behind an uncertified refill head.

After a retention cut, refill continues to name the current head. Later live acknowledgements cannot certify missing writes covered only by the refill. In [decision 0013a](decisions/0013a-remote-durable-acknowledgement.md), the head of an uncertified refill is the change position that its frame names; live confirmation starts strictly above that position.

The required integration regression writes immediately after a real three-region restore and verifies that rewriting an unchanged body changes its stored version. Fast coverage checks the complete-log refill position and subsequent live batch; the retained-log-cut negative control continues to reject remote-durability proof from an uncertified refill.
