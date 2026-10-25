# 0016: Strict plan database allowance across regions (escrow)

- **Status:** accepted, 2026-10-25. COS decided strict quotas through durable escrow (TS-995, TS-966); this record
  fixes the protocol before code. Revised after an adversarial review (database unit keys, clock-independent
  registration, batch and retry acquisition, the limits listed below).
- **Scope:** `Features/Management` (the allowance check where a database is registered), one new peer RPC on the
  internal listener.
- **Amends:** [plan-database-allowance.md](../plan-database-allowance.md) ("concurrent creates cannot both pass on one
  node") and [section 15](../replication/15-catalog-system-as-a-lattice-database.md) of the replication design ("limits
  can briefly overshoot by one concurrent create per region"). The plan database allowance no longer overshoots.

## Problem

The allowance is counted in the transaction that registers the database, against the local copy of the catalog. Each
region counts only what has replicated to it, so creates spread over gru, sjc and fra can all pass: on batch 8d a
Starter organization (5 databases) created a sixth. A local count can never be strict across regions without
coordination.

## Decision: bounded counter with escrow transfers

The allowance is a bounded counter (Balegas et al., "Putting consistency back into eventual consistency", 2015) kept in
the `_system` catalog, which every region holds. No leader is involved: every step is a local transaction in one region,
and transfers are pairwise.

### State (catalog collections, one document per record)

| Collection | Document | Written by |
|---|---|---|
| `quota_regions` | one per region: `registration`, a ULID taken when the region first registered | that region, once |
| `organizations/{org}/database_escrow` | one per region: `granted` (units it gave each region, cumulative) and `received` (the cumulative grants it has confirmed from each region) | that region only |
| `organizations/{org}/database_units` | one per database, keyed `{project}:{database}` as its record is: the region whose unit it consumed | the region that registered it, once |

Every escrow document has a single writer, so replication never merges two writers' values, and every number in it
only grows.

### Rights

For an organization with plan allowance `L` (as the region reads the plan), region `r` holds

```
rights(r) = genesis(r) + Σ_x max(escrow[x].granted[r], escrow[r].received[x])
                       - Σ_x escrow[r].granted[x]
                       - live(r)
```

- `genesis(r)` is `L` for the **genesis region**, the region with the earliest registration ULID, and 0 elsewhere.
- `live(r)` is the number of the organization's live databases whose unit `r` consumed. A database without a unit
  record (registered before this change) belongs to the genesis region.

`r` registers a database only when `rights(r) ≥ 1`, in the same transaction that writes the database record and its
unit record. The local count check (`live databases < L`) stays as well.

**Why this never overshoots (fixed plan, agreed genesis).** Everything `r` subtracts (its own grants, its own
registrations) is written only by `r`, so `r` reads it exactly. Everything `r` adds is a lower bound: grants and their
confirmations only grow, and a deletion seen late only keeps `live(r)` high. So `r`'s view of its rights is never above
its true rights, and a region that spends only when its view shows a unit never drives its true rights below zero.
Summed over all regions, grants cancel: `Σ rights = L - live databases`, so live databases stay at or below `L`. The
argument needs every region to agree on the genesis region and on `L`; the limits below are the cases where they may
not.

**Deletes return units** with no extra step: a deleted database leaves `live(r)` of the region that consumed its unit,
once that region sees the deletion. A database id is never reused, so a unit returns exactly once.

**Plan changes** move only the genesis region's share. An upgrade raises it. A downgrade lowers it, possibly below
zero, which the bound above does not cover: units another region already holds stay spendable there until that region
sees the organization at or above the new allowance through the local count. Regions ask for exactly the units they
lack, so units sit outside the genesis region only after a create there failed or a database there was deleted.
Organizations above the allowance keep their databases, as before.

### Transfer (peer RPC `terrabase.v1.DatabaseEscrow/Transfer`)

A region short of units asks peers for exactly what it lacks, before retrying its registration:

- **Request:** organization, the requesting region, `received` (what the requester already counts from this peer),
  `want`.
- **Giver,** in one catalog transaction (catalog commits sync to disk, [0012](0012-system-database-synchronous-full.md)):
  if its `granted[requester]` is above `received`, an earlier grant's reply was lost, and it answers that total without
  granting again. Otherwise it grants `min(want, rights)` (possibly 0) and answers its new cumulative `granted[requester]`.
  An organization the giver has not seen yet (created in the requester's own commit) is on Hobby, the default.
- **Requester:** records `received[giver] = max(received[giver], answer)` in its own catalog transaction, then retries
  the registration. Replication of the giver's escrow document gives it the same fact later.

Retries are idempotent: the cumulative total is the only thing exchanged, and a repeated request after a lost reply
returns the same total. A grant whose reply is lost is not lost either: it stays out of the giver's rights and arrives
with replication, and the requester rereads its rights before asking again. The requester asks the genesis region first,
then the other serving regions, each with a short timeout, until it holds every unit it lacks (a commit creating
several databases asks for all of them). Creates in the genesis region need no transfer while it holds units.

**Partitions.** A region that reaches no peer holding units refuses the create: `409 database_limit`, with a message
that the rest of the allowance is held in regions it cannot reach now and to retry. It never registers beyond its
rights. When every region was reached and none had a unit, the usual `database_limit` message applies.

### Regions

`genesis` must be the same region everywhere and stable, so it cannot follow the peers file, which drops a region while
its node is down. Each region registers once its catalog gate opens (and in any escrow transaction that finds it
unregistered), with a ULID after every registration it has seen, whatever its clock says. The registration set only
grows. A region added to a running cell therefore sees the earlier registrations through its catalog gate and never
becomes genesis. A region uses genesis units only while every serving region is registered, so regions that boot
together and see each other agree on genesis before any of them spends a unit.

## Consequences and limits

- **Rolling deploy.** Old nodes do not register or answer the RPC (`UNIMPLEMENTED` counts as unreachable). During the
  rollout that introduces this, new nodes refuse a limited plan's creates until every serving region runs it and has
  registered, since genesis units are not used while a serving region is unregistered. Old nodes keep the local count
  and are not fenced: creates on old nodes during the rollout can overshoot as before.
- **Databases from before escrow** have no unit record and are charged to the genesis region. Until the genesis region
  holds every such database in its catalog (replication or anti-entropy can deliver a registration before an older
  database), it can count too few. Databases registered by this build are charged exactly by the region that made
  them.
- **Storage.** New catalog collections only; no storage format change.
- **Availability.** Strictness costs availability: a region holding no units cannot create while partitioned from every
  region holding them. Creates outside the genesis region pay one cross-region round trip when they need a unit.
- **Known gaps.** A leaderless cell cannot close these without a consensus step, which this decision does not add:
  - A cell's first bootstrap split into components that never saw each other (a region registering and creating alone,
    then the others bootstrapping without it) can produce two genesis regions.
  - A downgrade while units sit outside the genesis region (see Plan changes).
  - A region whose node is rebuilt from an empty disk before its latest escrow or registration write replicated could
    reissue units it already spent.
  - Decommissioning the genesis region strands the units it holds.
  - A management commit that raises the plan (`set_plan`) and creates a database together, sent outside the genesis
    region, acquires units at the old plan; send the plan change in its own commit first.
- **Enterprise and platform** organizations are unbounded and never touch the escrow.
