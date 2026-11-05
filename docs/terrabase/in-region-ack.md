# In-region acknowledgement of local writes (PIVOT R10, D16)

A region with two discovered nodes acknowledges a local write only after it is durable on both nodes, in different
fault domains. Losing one node then loses no acknowledged write. A region with one discovered node uses a one-node
quorum, so local writes continue before N1 adds its holder. This is the user's 2026-11-05 availability decision,
superseding D16's previous one-node forwarding rule. Single-node acknowledgements have no second-node failure protection;
`durability=home` still waits for home acceptance. This note extends [local writes and the push](local-overlay.md).

## Why not the push stream alone

The push stream lives in the origin region's JetStream, which runs only on the region's founder, with one replica
per region (`unique_tag` `region:`, `docs/mesh-jetstream.md` in
ts-orchestrator). An in-region stream replica is therefore impossible, and the stream is on one host. The second copy
is held by TerraBase itself, on the region's second node.

## Nodes of a region

The peers file (`TS_PEERS_FILE`) lists every region's founder, then its fully joined workers (ts-orchestrator
`docs/mesh-jetstream.md`, per-node services). A region's first line is its founder and data owner (`ts-sjc-1`).
Every other region reaches only that node, for home forwarding, pulls, usage and listings.

Each other node of the region is a holder (`ts-sjc-2`). The role follows from the peers file alone: another node of
the same region comes before it. A holder stores no customer data:

- it follows no replica and answers no revision notice;
- it runs no push stream and no home consumer;
- it commits no local write;
- it holds the data owner's local writes and finishes their push after a node loss.

Client traffic reaches only the data owner. A region whose peers file lists one node has no holder and uses its durable
push stream as a one-node quorum. A configured partner that is unhealthy is not treated as an absent partner.

## Path of one local transaction

1. The node publishes the `RegionTransaction` to the push stream (R7) and gets its stream sequence.
2. If discovery lists a partner, it sends `POST /v1/sync/holds` to that partner's internal listener: origin node, home region, database, sequence and
   the transaction. The partner stores it in its system database (kind `local_holds`, key
   `<origin>.<organization>.<project>.<database>.<sequence:D20>`) with a transaction synced to disk, then answers 204.
3. The node commits the replica transaction and acknowledges the client.

If discovery lists no partner, step 2 is unnecessary and the local acknowledgement does not wait for home. If a
configured partner does not answer 204 within 2 s, the write is not acknowledged on one node. It is already in the
push stream, so it waits for the home's `applied` notice, as with `durability=home`, and answers with the home's result.

## Release

When a pull shows that the home's pages hold the database's transactions up to sequence `n`, the node purges its push
stream up to `n` and sends `POST /v1/sync/holds/release` with `n` to the partner, which drops what it holds up to `n`.
A lost release is covered by the next one, because each one covers everything before it.

## Health and the local-writes fact

Every second a node sends `POST /v1/sync/holds/<origin>/contact` to its partner, which answers 204 while its storage
is ready. The partner counts as healthy for 3 s after any answer.

`ILocalCommitPath.LocalAckAvailable` is the fact the router (R4) reads. It is true while the discovered regional quorum
is healthy, the push stream is ready and connected, and the clock is admitted. A discovered one-node region needs only
its own durable push; a region with a configured partner needs that partner to answer. Before discovery succeeds, or
if discovery fails, local acknowledgements are unavailable. The router commits a write locally when its shape allows
it, the clock is admitted (R12), and either `durability=home` was requested or `LocalAckAvailable` holds. Other writes
go home. The admin metric `terrabase.localwrites.available` follows the same fact.

## Node loss

A partner that holds transactions of an origin node which has not reached it for 10 s finishes that origin's push. It
applies each held transaction at its home with `POST /v1/sync/.../apply` (R6), one database at a time, in sequence
order. It drops a transaction once the home answers, and stops that database's round at the first failure, so order
holds.

The home answers a transaction it already applied from its record. So a transaction that both the partner and a
recovered origin's stream deliver is applied once. Each source delivers in commit order, and the origin commits a
database's next transaction only after the previous one is held or applied. So the home never sees a later
transaction of a database before an earlier one.

One case can apply out of order: the partner failed to hold a transaction, the client cancelled its wait for the home,
and the home was unreachable until both sources delivered. For each column, the greatest HLC stamp wins (R6), preserving convergence on the
same rows.

## Limits

A partner holds at most 65,536 transactions and 1 GiB. A full partner answers 503, and the write waits for the home.

## Metrics

| Metric | Meaning |
| --- | --- |
| `terrabase.localwrites.available` | 1 while this node can acknowledge on its discovered regional quorum |
| `terrabase.localack.partner` | 1 while the in-region partner answers |
| `terrabase.localack.held` | transactions this node holds for its partner |
| `terrabase.localack.finished` | held transactions this node applied at the home after their origin was lost |

## Live deployment

ts-orch runs TerraBase on each region's second host (`ts-<region>-2`) as its holder and lists both nodes of every
region in the peers file. Client traffic reaches only the founder.
