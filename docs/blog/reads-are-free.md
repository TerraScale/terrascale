# Reads are free: how (stub)

> L-5 launch-post stub. Full post lands with the edge measurements (X-5, D-61).

- Writes and storage are the only billed dimensions (D-16); reads, queries,
  egress, fan-out, and subscribers are free and unbilled.
- How: shared result views encoded once into an immutable HTTP log that
  Cloudflare caches and request-collapses; a cache hit runs zero metered
  compute (D-55). A write is billed once no matter how many views or readers
  it reaches (D-22).
