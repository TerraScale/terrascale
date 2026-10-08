# Collection change feeds (TS-689)

`GET /projects/{project}/databases/{database}/collections/{collection}/changes`
requires the client TLS listener and a ts-auth JWT with `documents:read` reaching the project and database.
The route is unversioned for the closed beta.

This first slice exposes the existing SQLite history as a bounded polling feed. Both inserts
and updates produce `upsert` with the stored document. Document deletion produces `delete`
with a null document. Repeated writes to a document within a committed transaction remain
separate events. Rolled-back writes and replicated states that lose the merge produce no events.

```json
{
  "changes": [
    {"operation":"upsert","id":"019...","offset":"12","sequence":"17","document":{"name":"Ada"}}
  ],
  "resume_token": "alpha.<lineage>.<offset>.<sequence>",
  "has_more": false
}
```

Poll with `resume_token` from the previous response, including empty responses. Without a token,
the feed starts strictly after the retained history floor; it does not provide an initial snapshot.
The optional `limit` is 1–100 (default 100). Events are ordered by local commit offset and append
sequence. Page boundaries can split a commit; consumers needing atomic application should group
by offset. A full page resumes after its last event. Once the read snapshot is exhausted, the token
advances to the database head. Later writes appear on the next poll.

Ordering and tokens are specific to a serving member. Winning replication imports receive a local
history offset and appear in that member's feed; replicas need not observe the same order. Pin the
consumer to its serving member. No global ordering or cross-member cursor portability is promised.

Tokens bind organization, project, database, collection, database incarnation and collection
generation. Restart preserves the lineage. Collection recreation, database copy/restore or replacement
starts a new lineage. A malformed/noncanonical token returns `400 invalid_resume_token`; a token
for another lineage, below the retained floor or beyond the current head returns `409 resume_expired`.
A missing collection returns `404 unknown_collection`. Collection drops are lifecycle boundaries,
not a stream of synthetic document deletions: reacquire the collection and start a new feed.

Each page reads at most 100 events and 2 MiB of stored JSON plus a conservative per-event framing
allowance. The engine checks sizes before materializing bodies and validates retained/restored JSON.
The service admits at most 16 responses and reserves 32 MiB per response from the shared foreground
memory budget before opening storage. The reservation covers decoding, validation and response
serialization and lasts through response delivery, so slow consumers retain admission. Exhaustion
returns `429 change_feed_busy`; consumers should retry with their previous token. Cancellation and
errors release admission. Pages carry `Cache-Control: no-store`.

Metrics on `TerraScale.TerraBase`: `terrabase.change_feed.pages`, `terrabase.change_feed.refused`, and
`terrabase.change_feed.active`, with no tenant labels.

History and document state commit atomically to the same SQLite file. The feed introduces no queue,
external store or schema migration. Ordinary writes retain the existing `synchronous=NORMAL` path;
this slice does not strengthen its power-loss durability guarantees.

## Remaining TS-689 slices

A configurable retention window with bounded pruning and explicit expiry, and distinct insert/update
event kinds, remain follow-up work. Existing history is not pruned by this first slice. Realtime
subscriptions and webhook delivery can build on the polling contract; neither is implemented here.
