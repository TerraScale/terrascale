# Client reads during replica recovery

Document GETs, query/list pages, collection metadata, collection lists and declared schema reads return `503 copy_faulted` while their local database copy is faulted or awaiting rebuild certification. Admission checks the copy before reading and checks the original incarnation again before returning a result.

Tenant route admission also refuses new changes-feed requests while their local copy is refused. Revalidating an already-open feed's incarnation remains a separate feed-layer finding in the R6 review report.

Document and query cache entries include that admitted incarnation. A certified replacement therefore cannot reuse entries from the retained copy, including entries populated by an older in-flight request. Ordinary writes continue invalidating cache entries through write stamps.

Salvage also checks that every registered collection's materialized body, metadata body and hidden uniqueness-conflict body has replicated state. A missing state refuses certification and preserves the original side copy unchanged, even when source refill succeeded. Operators must retain that evidence for reviewed recovery; the rebuild cannot reconstruct a lost state from a body alone.

This changes refusal behavior for damaged copies, without changing public routes, the replication wire protocol or persisted formats. Existing FULL refill and completion durability barriers remain in place.
