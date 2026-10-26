# Workspace erasure foundation

Cleanup uses bounded three-ULID database tombstones, scoped 32-record SDK query
pages, owner-only workspace projections, CAS deletes and account/project/org
replay. HTTP fixtures exercise the real adapter/SDK against synthetic producers.
Interactive workspace persistence and lifecycle marker creation are not connected
yet; activation awaits placement-Sync and pending catalog integration review.

The foundation's Fast HTTP cases cover the real adapter/SDK query cursors and
conditional deletion, 4,100 idempotent bounded markers without organization JSON
growth, large-record projection/isolation, retained tombstones after a late write
and HTTP 503/retry, and account/project/org erasure. These are contract fixtures,
not live TerraBase qualification. Their runtime execution remains pending CI.
