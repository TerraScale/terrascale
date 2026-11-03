# Transaction capacity refusals

A transaction's changes are encoded into one bounded replication change group before
commit. The group holds at most 1,024 changed documents and, in total across the whole
transaction, at most 2 MiB of UTF-8 collection names, document ids, document JSON and
the encoded per-document states the engine stores beside each document (`IStorageTransaction`
`MaxChangeGroupChanges` and `MaxChangeGroupBytes`). The encoded state is a compact binary
form of the document's field lattice, so it adds bytes beyond the JSON alone; a request
body under the 4 MiB route limit can still exceed the 2 MiB encoded budget.

A transaction that exceeds the budget is refused before its commit: nothing committed,
every operation rolled back, the offset unchanged. `POST /v1/projects/{project}/databases/{database}/transactions`
answers `503` with `error.code` `capacity`. When the refusal is attributable to one
operation — that operation's own encoded state crossed the remaining budget — the error
carries that operation's index in `error.op`. When the budget is crossed only by the
transaction as a whole (the sum of operations that each fit alone), `error.op` is absent
and the message names the whole-transaction budget. Either way the refusal is
deterministic: the same request fails the same way until it is split, and it is safe to
rebuild and resend smaller transactions, unlike a `write_outcome_unknown` answer, which
concerns a commit whose outcome is genuinely uncertain and must never be replayed
automatically.

Clients that build large transactions should split them below the budget, for example
by document count or by summing serialized document sizes; documents near or above the
budget alone cannot be written in a transaction at all.
