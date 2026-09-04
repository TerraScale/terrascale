# Replication design (stub)

> L-5 launch-post stub. Design reference: terrabase ADR 0044; decisions
> D-35/D-36/D-37/D-45.

- Inside a region: RF=2 physical WAL-frame follower; `RegionalDurable` on the
  follower's fsync ack.
- Across regions: leaderless active-active; journal tailer over QUIC, causal+
  apply, document-level CRDT merge (remove-wins per field, HLC tiebreak).
- Anti-entropy by range-based set reconciliation; session tokens for
  read-your-writes; `GlobalDurable` fails closed below 2 reachable regions.
