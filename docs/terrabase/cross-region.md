# Cross-region replication (TS-643)

> **Removed (2026-10-11, slice R2a).** Client databases no longer replicate this way: the sender, receiver,
> anti-entropy and write-wait described here are deleted, and design C replaces them
> ([multi-region-replication.md](multi-region-replication.md)). Until slice R4a ships, client databases do not
> replicate between machines. Catalogs (`_system`) are per machine until R10a.

A write accepted in one region becomes readable in every other region of its replication group, with the same version
token. This is stage 1 of [anna-core §10](design/anna-core.md#10-stage-2-later) on today's engine: the simpler route
now, with every simplification marked `anna-full (TS-643)` in the code.
- Each region keeps its own members, ring, leases and catalog. Nothing in-region waits for another region: a client
  write answers after its region's write-wait only, and replication across regions is asynchronous.

A member contacts other regions through gRPC `Wan.Ask` on private peer listeners.
The current membership transport must be verified during the ts-orch migration;
see [OCI hosting](../oci.md).

## How it works

Every member of a region **pulls** each other region's change groups:

1. **Heads.** Once a second, a member asks every member of the other region for its heads. Each answers
   with the head of each origin it holds: one origin per database that it wrote to, with the sequence of its last
   committed change group. More than 4096 heads, or a page reaching the 960 KiB protobuf reply budget, page through
   that member. The whole `HeadsReply` travels in the existing versioned `TBLZ4E01` LZ4 application
   envelope ([0007](decisions/0007-lz4-application-envelope-replication.md)). Its header, encoded payload and decoded
   payload checksums are validated before parsing any heads, membership or EOF evidence. A reply that fails integrity
   validation cannot authorize adoption, placement changes, cursor progress or deletion, even when its ULIDs would still
   parse as valid protobuf. Encoded and decoded heads replies are each bounded to 1 MiB; the reply budget leaves room
   for the envelope header and LZ4 expansion.

   The answer also lists every member the answering member sees in its region, so the asking member also asks the
   members its own view of that region missed.
   It follows member lists from every reply and later page until each discovered member has been asked once that round,
   since members can hold different views during a membership change. At most eight members are asked concurrently and
   a round admits at most 1024 current identities and attempts at most 1024 direct jobs. Discovery has a ten-second deadline, including pagination; free request slots
   are refilled as replies finish. Scattered first replies are collected before continuations and direct requests share
   the same queue, so stalled pagination cannot block reply consumption. Unqueried identities carry over to the following
   round ahead of failed attempts. This queue holds at most 1024 unresolved identities per remote region, independently
   of current advertisements, so historical silence cannot reject useful new heads. New identities get request priority
   when current and historical identities exceed the queue budget. Each unresolved identity must answer completely
   before cleanup, even if newer replies omit it. A departed member that never answers therefore
   keeps cleanup postponed for this puller's lifetime. The heads protocol carries names without membership epochs,
   expiry or retirement evidence; cached views and later omissions cannot authorize retirement. Reclaiming these
   identities requires an authoritative remote retirement contract. If the carryover exceeds its bound, identities with
   pagination progress take priority; discarding unresolved identities permanently blocks cleanup for that puller. When the deadline expires, collected heads can
   still be pulled, but the round remains incomplete. Invalid scattered replies are ignored without discarding healthy
   heads, while keeping the round incomplete. A page that exceeds the accumulated member limit is rejected before its
   heads or members are added; earlier accepted pages remain useful. Exceeding the current admission limit across
   responders retains usable heads but blocks cleanup. Pagination accepts at most sixteen pages per member per round
   and retains a 48-byte cursor per unresolved member between rounds. The only heads kept are those still behind after
   the pulls, at most one heads reply's worth, retried next round because a resumed scan does not advertise them again;
   a fresh head from the same member for that database supersedes them whatever its origin, a member answering that it no
   longer holds the origin drops them, a sweep that certifies their database absent drops them, and they
   never count as sweep evidence. A round holds at most 65,536 heads and 16 MiB of heads replies across every member and
   page; a page past that budget is not accepted, but the members it lists are, its member stays unresolved and its cursor
   stays after the last accepted page. Scattered first pages may take at most half of it, so saved cursors and deferred members always get the rest,
   and members served pages in a round queue behind the others in the next. A round that no member answers still pulls
   its retries, without certifying cleanup or catch-up.
   Every head must follow the requested
   cursor and the head before it; a misordered page taints the sweep before any cursor past it is saved.
   A sweep keeps one local catalog
   page of at most 100 deletion candidates, their witnessed presence in accepted heads, and at most 1024 member EOF
   certificates. Resumed pages contribute to this same proof. Only after every discovered member reaches EOF can
   cleanup use the fixed candidates and advance the local catalog cursor; staleness resets only if all accepted
   pages also reached their observed heads. Missing members keep the sweep incomplete and staleness growing.
   EOF certificates do not suppress fresh pagination while other members wait. Once every listed member holds one,
   a deadline that cuts a fresh scan after the scattered replies ends the sweep like that member failing to answer. Malformed evidence taints the sweep;
   its eventual EOF starts a fresh scan of the same catalog page without cleanup or a staleness reset.
   Sweep proof, carryover and the overflow cleanup block are in-memory; restarting begins a fresh sweep.
2. **Pull.** Independent database targets are prepared up to eight heads ahead, while pulls remain in heads order.
   Duplicate database ids and names within a project prepare one at a time. With at least 32 queue slots per remote,
   current targets and lookahead use at most a sixteenth of `storage-queue` across all remotes; smaller queues keep inline
   preparation. Every started preparation finishes before the next round, and registration is checked again before
   pulling a prepared target.
   For each origin it is behind on, the member asks the member that holds it for the change groups after its own
   applied prefix.
   Replies hold whole groups, LZ4-compressed in the same versioned envelope as in-region replication
   ([0007](decisions/0007-lz4-application-envelope-replication.md)), up to 960 KiB. A larger group crosses in parts
   of its envelope and is applied once complete.
3. **Apply.** Each group goes through the in-region receiver's journal path (`IReplicationJournal`): in order after the
   applied prefix, idempotent, in one synced SQLite transaction with its applied prefix. A duplicate changes nothing; a
   gap is refused and pulled again next round.
4. **Join.** Groups join exactly as in-region groups do ([replication](replication.md#what-replicates)): each delta joins
   the stored register, and reads take its last-writer-wins write (the greatest stamp, then replica id). Concurrent
   writes in two regions settle on the same winner in both. `anna-full (TS-643)`: replica ids number regions too.

A round that pulled something and is still behind starts the next one at once; otherwise the next one starts a
second later. Each origin gets at most 16 pull requests per round, so a busy origin does not hold up the others.

The messages use the protobuf schema `Features/Replication/Protos/terrabase/v1/wan.proto`, with WAN request version 1.
Raw protobuf heads replies are rejected, with no fallback or migration. Regions must use version 1 to replicate with
each other; a version mismatch postpones replication and cannot authorize inventory cleanup. The number never
increases (user rule, 2026-10-09): a wire format change ships at a rollout that stops both regions' members before
either starts the new build.
Pull replies carry the same groups of register deltas (`DeltaGroup`) as in-region replication, in their protobuf
framing and versioned envelopes. Every request and encoded reply is at
most 1 MiB.

### Version tokens across regions

A document's version (its `ETag`) hashes its state under the database's organization, project and database ids
([replication](replication.md), TS-574). A database replicates only under the same ids in every region, so the same
state has the same version everywhere: a version read in one region satisfies `If-Match` in another once that region
holds the state. The `tbv2` causal tokens of TS-647 step 4 replace these and stay valid across regions the same way.

## Placement

Every database has a placement: its **home region**, where it is created and managed, and its **replica regions**, the
other regions that hold a full copy. Every region of the placement serves reads and writes, and writes replicate
between them. The Management API sets it on create and changes it with `PATCH`
([region placement](management-api.md#region-placement)); a database created through the data API, or recorded before
placement existed, lives in its home region only.

How the regions learn it:

- **Heads.** Every member's heads list every database it holds a file of, written to or not, with its name and
  placement. The home region numbers its placement changes; another region takes a placement with a higher number.
- **Recording.** A region that registers the database's organization and project under the same ids records the
  database. Each machine records it in its own catalog from the heads, with the same id and name.
  - A region in the placement also creates the file and pulls the database's origins.
  - Any other region keeps only the record, so its API can answer `421 wrong_region` with the regions that serve the
    database, and `ts-replay: region=<home>,<replicas>` behind the edge ([ts-orch.md](ts-orch.md#ts-replay)).
  - Data goes only where the placement says: a member serves a database's groups only to regions its placement names
    (`PullRequest.region`).
- **Removal.** A region removed from the placement stops serving the database at once. It keeps its copy, unserved and
  no longer pulled, so the writes it accepted keep reaching the others; added back, it resumes from that copy.
- **Deletion.** A database is deleted in its home region once it has no replica regions. Every other region deletes its
  record and any copy when a complete sweep finds the database in none of the home region's heads; a region never brings
  back a database it deleted (its id stays taken). Each sweep reads at most 100 deletion candidates from a snapshot of
  the local catalog before asking for the home region's heads, retaining that page through any resumed remote pages
  and advancing to the next catalog page only after completion. This also
  removes deletions missed during a restart, including records without a local file. No member answering, or any listed
  member missing, postpones cleanup and keeps the cursor. The storage reader is released before network calls and cleanup,
  so reconciliation works with `storage-queue=1` too.

A database another region holds is not recorded here when:

| Reason | Metric |
| --- | --- |
| this region does not register its project under the same ids, or it was deleted here | `terrabase_wan_unregistered` |
| another database of its project has its name here | `terrabase_wan_conflicts`, plus a warning: rename or delete one of them |

`terrabase_wan_unregistered` also counts databases recorded here whose placement does not name this region.

## Automatic region discovery

The operating contract discovers serving regions automatically. Database placement
and residency restrictions still determine which data may replicate; discovering
another region grants neither placement nor authorization. A named replica region
pulls the database once its admitted members start. OCI Vault owns runtime secrets;
CI credentials remain GitHub Actions secrets. See the [operating contract](zero-configuration.md)
and [hosting implementation limits](../oci.md).

## Metrics

On the `TerraScale.TerraBase` meter, scraped at `/metrics`. `region` is a configured region, never a database or member.

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_wan_lag{region}` | Change groups that the members of that region that answered this member's last round committed and this member has not applied; capped at `long.MaxValue` |
| `terrabase_wan_staleness_seconds{region}` | Seconds since this member completed a clean inventory sweep and reached every accepted head, including pages from earlier rounds; near 0 when caught up. An unresolved member makes it grow even while `lag` reads 0 |
| `terrabase_wan_members{region}` | Members of that region whose entire requested heads sequence reached EOF in this member's last round |
| `terrabase_wan_unregistered{region}` | Databases that region holds that do not replicate here: their project is not registered here, or they were deleted here |
| `terrabase_wan_conflicts{region}` | Databases that region holds that do not replicate here because another database of their project has their name here |
| `terrabase_wan_pulled_groups_total{region}` | Groups pulled from that region and applied |
| `terrabase_wan_pulled_encoded_bytes_total{region}` | Envelope bytes pulled from that region, after LZ4 |
| `terrabase_wan_served_groups_total` | Groups this member served to other regions |
| `terrabase_wan_failures_total{region,reason}` | `heads` (no member answered), `pull` (a request failed), `apply` (a group was refused), `ahead` (this member applied groups an origin never committed) |

Alert on `terrabase_wan_staleness_seconds` above a minute, and on any `ahead` failure.

## Failure modes

| Failure | Effect | Handling |
| --- | --- | --- |
| Link between regions down | Each region serves its own reads and writes | Rounds fail (`heads`); staleness grows; after the link heals, pulls resume from each applied prefix. Journals are never truncated, so any partition length heals |
| A member of the other region down | Its origins' groups wait | The other members' origins keep flowing; its origins resume when it is back |
| A group refused (invalid, or a dot with a second value) | That origin stops | Counted as `apply` and pulled again every round; nothing of the group is applied |
| An origin ahead of its member's head (a database file replaced by hand) | That origin stops | Counted as `ahead`; restore through import, as in-region |
| Clock skew | Last-writer-wins favours the fast clock between concurrent writes | A stamp further ahead than 1 s is applied and counted, and moves the member's clock at most that far |

## Not yet (`anna-full (TS-643)`)

- Every member pulls every remote origin itself, so a group crosses the WAN once per receiving member. Full Anna pulls
  once per region and joins the deltas into the cell.
- A group whose origin member loses its disk before the other region pulled it reaches that region only through a later
  write of the same document: other members of its own region hold the state, not the group. A repair pulls the state
  from a surviving member. It needs a read of every document's state, tombstones included, which the engine does not
  offer yet; it comes with TS-647 step 4's snapshots for refilled replicas.
- The home region cannot change. A removed region keeps its copy until the database is deleted; full Anna drops it once
  every other replica holds its writes. Plans do not limit placements yet.
- Catalogs and API keys are per region.
- A member reads every database's head for each heads request, and pulls one origin at a time.
- Interval and tombstone GC (TS-647 step 7) must keep a group until every region applied it: remote regions read origin
  journals, which nothing truncates today.

## Tests

- **Fast** (`CrossRegion/CrossRegionTests.cs`) covers:
  - both directions, and versions valid across regions;
  - a partition and its heal;
  - concurrent writes;
  - a group larger than one reply;
  - each placement (gru only, sfo only, both), the `421 wrong_region` answers and their headers;
  - replica regions removed, added back and the database deleted, which the other region follows; name clashes counted;
  - placement checks on create and through commits; databases of a project only one region registers;
  - a two-member region reached through single replies, and staleness when one of its members goes silent.
- **Fast** (`CrossRegion/CrossRegionDeletionTests.cs`) covers deletions at home while the other region is stopped, for both
  replicas and records without a file; a partition postpones cleanup until the link heals, with `storage-queue=1` after
  restart.
- **Fast** (`CrossRegion/CrossRegionMembershipTests.cs`) covers differing member views, discovery through direct replies
  and later heads pages, cyclic lists, and preservation of a replica while a transitively discovered member is silent.
- **Fast** (`CrossRegion/CrossRegionDiscoveryBudgetTests.cs`) covers discovery expiry with a large unreachable member list,
  retention of partially paged heads, joining canceled requests before pulling, and refilling slots while slow members wait.
- **Fast** (`CrossRegion/CrossRegionPagingProgressTests.cs`) covers healthy new heads beside a saturated historical queue,
  and tail replication through saved cursors when every full scan exceeds the deadline, without healing or faster pages.
  `CrossRegionPagingProgressTests.Sweep.cs` covers deletion qualification beyond sixteen heads pages and advancement to the
  next local catalog page while preserving replicas witnessed on earlier remote pages, continued paging by an
  earlier-finished member beside a silent member, the sweep ending when that member finishes while the other's fresh
  scan hits the deadline, misordered pages that would skip a database, retrying a
  head left behind on a resumed page without reviving a database certified deleted, the round's heads budget across
  members, saturating scattered first pages, retries during unanswered discovery, retries
  for an origin their member no longer holds, and malformed prefix/head preservation and healing.
  `CrossRegionPagingProgressTests.Preparation.cs` covers bounded independent cold preparation, inline admission with
  `storage-queue=1`, and deletion or placement removal between target preparation and its ordered pull.
- **Slow** (`CrossRegion/CrossRegionPartitionTests.cs`) is the partition suite:
  - two regions of two members each;
  - a link that drops, delays, duplicates and corrupts replies, with a partition and a heal;
  - member restarts on both sides, while writers on every member put and delete;
  - checks that every member converges on the same state, that every acknowledged write nobody else wrote is
    there, and that every document holds a value some writer sent.
  - The seed it prints repeats a run.

## Live check

`scripts/terrabase/cross-region-check.py` writes a unique value to a dedicated canary database through each region's public API
and reads it back through the other, in both directions. It also writes one document in both regions at once, which
must converge. It exits non-zero on a mismatch, or when a write takes longer than the lag threshold to show up. Setup
and the exact command are in the [rollout](rollout/ts-643-cross-region.md).
