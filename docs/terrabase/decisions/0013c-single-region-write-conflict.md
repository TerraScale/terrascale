# 0013c: A database left in one region refuses writes with a conflict

- **Status:** accepted, 2026-10-23. Amends [0013a](0013a-remote-durable-acknowledgement.md) section 5, "Placement and
  availability", for one case only.

## Context

- Under 0013a a database is placed in at least two regions. A `PATCH` may still remove every replica region, as the
  step before a delete.
- Such a database then takes no writes. 0013a answered them `503 remote_region_unavailable` with `Retry-After: 1`,
  the answer for a passing outage. Here no retry can succeed until a placement update adds a region, so SDK and
  customer retry loops spun forever on a permanent state.

## Decision

- A write to a database whose placement names no replica region and is not `allRegions` is refused before it executes
  with `409 placement_needs_two_regions`, without `Retry-After`. The attempt executed nothing.
- Adding a replica region (or `allRegions: true`) through the placement update ends the refusal.
- Unchanged: `503 remote_region_unavailable` with `Retry-After` still answers a database placed in two regions or more
  while no machine of another placed region is connected, and an `allRegions` database while no other region serves.
  Those states pass on their own.
- Unchanged: a cell of one (no member directory) admits every write.

## Restore anchor wait

- A request that removes or deletes catalog state is acknowledged only once the restore anchor records it
  (`catalog_anchor_unavailable` otherwise, docs/terrabase/restore-drill.md).
- Such a request now waits for an anchor write already in flight (the periodic one or another change's) within
  the same 10 s bound, rather than answering `503 catalog_anchor_unavailable` at once. That instant refusal made a
  committed `PATCH` or `DELETE` report failure whenever it met another anchor write.
