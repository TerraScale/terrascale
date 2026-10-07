# Cross-region replication (TS-643)

A write accepted in one region becomes readable in every other region of its replication group, with the same version
token. This is stage 1 of [anna-core §10](design/anna-core.md#10-stage-2-later) on today's engine: the simpler route
now, with every simplification marked `anna-full (TS-643)` in the code.

- Each region keeps its own members, ring, leases and catalog. Nothing in-region waits for another region: a client
  write answers after its region's write-wait only, and replication across regions is asynchronous.
- Regions talk only over ts-orchestrator's NATS fabric (TS-562), through core NATS request/reply in the fabric's
  cross-link `query` family. No region connects to another region's peer listener.

## How it works

Every member of a region **pulls** each other region's change groups:

1. **Heads.** Once a second, a member asks `ts.orch.v1.<cell>.query.terrabase-wan.heads`. Every member of that region
   answers with the head of each origin it holds: one origin per database that it wrote to, with the sequence of its
   last committed change group. More than 4096 heads page through the member's own subject.
2. **Pull.** For each origin it is behind on, the member asks the member that holds it, on
   `ts.orch.v1.<cell>.query.terrabase-wan.member.<node-name>`, for the change groups after its own applied prefix.
   Replies hold whole groups, LZ4-compressed in the same versioned envelope as in-region replication
   ([0007](decisions/0007-lz4-application-envelope-replication.md)), up to 960 KiB. A larger group crosses in parts
   of its envelope and is applied once complete.
3. **Apply.** Each group goes through the in-region receiver's journal path (`IReplicationJournal`): in order, chained to
   the applied prefix by its journal digest, idempotent, in one synced SQLite transaction with its applied prefix. A
   duplicate changes nothing; a gap or a digest that does not chain is refused and pulled again next round.
4. **Merge.** Groups merge exactly as in-region groups do: last writer wins by merge policy v1 (the higher version,
   then a deletion over a live document, then the greater JSON bytes). Concurrent writes in two regions settle on the
   same winner in both.

A round that pulled something and is still behind starts the next one at once; otherwise the next one starts a
second later. Each origin gets at most 16 pull requests per round, so a busy origin does not hold up the others.

The messages are protobuf, `Features/Replication/Protos/terrabase/v1/wan.proto`, version 1. Every request and reply is
at most 1 MiB, NATS's default `max_payload`, which the leafnode links between regions keep.

### Version tokens across regions

A document's version (its `ETag`) hashes its state under the database's organization, project and database ids
([replication](replication.md), TS-574). A database replicates only under the same ids in every region, so the same
state has the same version everywhere: a version read in one region satisfies `If-Match` in another once that region
holds the state. The `tbv2` causal tokens of TS-647 step 4 replace these and stay valid across regions the same way.

## Registering a database in both regions

Only databases that **both** regions' catalogs register under the same organization, project and database ids
replicate. Register one in a second region with a [management commit](management-commits.md) that chooses the ids it
has in the first:

```json
{"id":"<new uuid>","ops":[
  {"op":"create_organization","id":"<organization id>","name":"<name>"},
  {"op":"create_project","organization":"<organization id>","id":"<project id>","name":"<name>"},
  {"op":"create_database","organization":"<organization id>","project":"<project id>","name":"<database name>","id":"<database id>"}]}
```

Leave out the operations for an organization or project that region already has. API keys stay per region: issue
one in each region (`POST /admin/api-keys` on the region's catalog primary).

A database another region holds that this region does not register is counted in `terrabase_wan_unregistered` and
never created here.

## Configuration

| Setting | Meaning |
| --- | --- |
| `wan-regions` | Every region of the group as `region=cell`, this node's `region` included: `gru-1=gru,sfo-1=sfo`. The same value serves every member of every region. Requires `nats-url` and a `node-name` that is a NATS subject token (lowercase letters, digits and inner hyphens, at most 63 characters) |

Unset, the node does not replicate across regions.

### What ts-orchestrator provides

Before `wan-regions` works between two regions:

1. The regions' NATS clusters are linked (`nats.embedded.interregion`, leafnodes rooted at gru).
2. The interregion contract exports `ts.orch.v1.<cell>.query.terrabase-wan.>` as a service to every peer region.
3. The terrabase role may subscribe to `ts.orch.v1.<own cell>.query.terrabase-wan.>` and publish to
   `ts.orch.v1.<peer cell>.query.terrabase-wan.>` (requests) and `_INBOX.>` (replies).

## Metrics

On the `TerraScale.TerraBase` meter, scraped at `/metrics`. `region` is a configured region, never a database or member.

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_wan_lag{region}` | Change groups that region's members committed and this member has not applied, as of its last round |
| `terrabase_wan_staleness_seconds{region}` | Seconds since this member last held every group that region reported; near 0 when caught up |
| `terrabase_wan_members{region}` | Members of that region that answered this member's last round |
| `terrabase_wan_unregistered{region}` | Databases that region holds and this region's catalog does not register |
| `terrabase_wan_pulled_groups_total{region}` | Groups pulled from that region and applied |
| `terrabase_wan_pulled_encoded_bytes_total{region}` | Envelope bytes pulled from that region, after LZ4 |
| `terrabase_wan_served_groups_total` | Groups this member served to other regions |
| `terrabase_wan_failures_total{region,reason}` | `heads` (no member answered), `pull` (a request failed), `apply` (a group was refused), `ahead` (an origin went back in time) |

Alert on `terrabase_wan_staleness_seconds` above a minute, and on any `ahead` failure.

## Failure modes

| Failure | Effect | Handling |
| --- | --- | --- |
| Link between regions down | Each region serves its own reads and writes | Rounds fail (`heads`); staleness grows; after the link heals, pulls resume from each applied prefix. Journals are never truncated, so any partition length heals |
| A member of the other region down | Its origins' groups wait | The other members' origins keep flowing; its origins resume when it is back |
| A group refused (invalid, does not chain) | That origin stops | Counted as `apply` and pulled again every round; nothing of the group is applied |
| An origin went back in time (restored from a snapshot) | That origin stops | Counted as `ahead`; restore recovery is required, as in-region |
| Clock or version skew | None | Versions are commit offsets, not wall clocks; an import more than 2³² ahead is refused, as in-region |

## Not yet (`anna-full (TS-643)`)

- Every member pulls every remote origin itself, so a group crosses the WAN once per receiving member. Full Anna pulls
  once per region and joins the deltas into the cell.
- A group whose origin member loses its disk before the other region pulled it reaches that region only through a later
  write of the same document: other members of its own region hold the state, not the group.
- Registration in a replica region is a manual commit; a database's home region and replica regions, chosen through the
  API and dashboard, come later. Deleting a database in one region does not delete it in the other.
- Catalogs and API keys are per region.
- A member reads every database's head for each heads request, and pulls one origin at a time.
- Interval and tombstone GC (TS-647 step 7) must keep a group until every region applied it: remote regions read origin
  journals, which nothing truncates today.

## Live check

`scripts/cross-region-check.py` writes a unique value to a dedicated canary database through each region's public API
and reads it back through the other, in both directions, plus one document written in both regions that must converge.
It exits non-zero on a mismatch or when a write takes longer than the lag threshold to show up.
