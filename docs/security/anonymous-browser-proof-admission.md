# Bounded anonymous browser proof admission

Anonymous consent uses one encrypted pool in the existing auth-flows collection.
The pool admits at most 256 outstanding proofs across all dashboard replicas.
Every admission prunes expired entries and commits the resulting pool with a
version precondition. Concurrent writers retry a bounded number of conflicts;
capacity or contention fails closed. Discarding a cookie cannot create additional
documents or exceed the pool capacity. Expired payloads are physically removed
when the next admission or removal commits; idle retention is at most one bounded
document, rather than an ever-growing collection of expired records.

Protection uses the existing AuthFlows AES-GCM boundary and OCI session key.
There is no new cipher, key store, per-process limiter, runtime flag or tuning
variable. This bound applies independently of MCP client-registration admission.
Invitation pages without actionable forms issue no browser proof. Persistent
session-bound proof issuance requires verified session or deletion recovery state;
a syntactically valid cookie alone cannot authorize an unbounded write.

The HTTPS integration journey uses two replicas and an injected clock, races the
last admission slots, verifies refusal without writes and reclaims abandoned
encrypted payloads on subsequent admission. No visual flow change needs captures.
