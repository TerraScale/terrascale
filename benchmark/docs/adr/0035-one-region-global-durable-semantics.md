---
date: 2026-08-29
authority: ../../../GOAL.md
---

# `GlobalDurable` requires multiple Regions

## Decision

`GlobalDurable` is a cross-Region durability class. It is accepted only when
at least two selected Regions are active and the row plus every causal
dependency is durable in all of them. A request with fewer than two active
selected Regions returns a typed refusal before acknowledgement.

A database with one selected Region may request `RegionalDurable`, which waits
for the local RF=2 physical WAL follower, or `BackupDurable` when its backup
contract is configured. Neither class implies protection from complete loss of
the only Region.

This rule is part of admission and the public contract. It applies uniformly
to the member process, generated contracts, SDKs, and dashboard. No local
two-member acknowledgement may be renamed `GlobalDurable`, and no retry may
silently weaken the requested durability class.

The leaderless replication and causal dependency mechanics are defined by
ADR 0044. This ADR supplies the fail-closed cardinality rule that those
mechanics enforce.
