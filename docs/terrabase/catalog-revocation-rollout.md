# Revocation witnesses during a regional rollout

A newly seeded region must hold every external catalog revocation cut before it serves. A whole peer snapshot transfers both the catalog states and their causal coverage atomically; a marker or matching bucket digests alone provide no coverage.

An unclean restart changes a peer's replica epoch. Its authenticated catalog anchor can still prove a specific cut from its previous epoch when the file incarnation matches and the catalog head at startup reaches the anchored head. After those checks, the peer records exactly that cut durably in the same file's applied vector, checking the incarnation again under the writer. A pinned whole snapshot can then carry it to the new region. This does not prove any later acknowledged tail of that epoch.

A mismatched incarnation, an older catalog head, or an unreadable anchor supplies no such proof. Restored majorities remain closed when every available catalog lacks an externally witnessed revocation.

The internal catalog-gate status includes the active anchor-listing, anchor-reading, coverage-checking or snapshot-import phase. Snapshot logs report import results and missing replica cuts without catalog contents or credentials.
