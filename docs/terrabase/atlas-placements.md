# Atlas placements

TerraBase tells [Atlas](region-identity.md) (the Cloudflare Worker that routes SDK
clients) which region homes each database. TerraBase is the source of truth; Atlas only
keeps what it is told.

## What is published

A placement per database: `{"v":1,"project","database","region","updated"}`. `project`
is the project id used in SDK routes (`/v1/projects/{project}/databases/{database}`),
and `database` is the database name, never its internal ULID. `region` is
the database's home region id (`ManagedDatabase.HomeRegion`, or the local region while
null); `region: null` means the database was deleted. `updated` is unix seconds and must
be strictly increasing per (project, database) as Atlas sees it, so a re-queued row uses
`max(now, last published + 1, previous queued + 1)`. The last published timestamp is
persisted per (project, database) in a separate system catalog record kind,
`atlas_placement_published`. Publishing a live placement retains it after deleting the
outbox row; only settling that database's tombstone deletes it. A newer pending row
prevents an older tombstone from deleting its timestamp.

Placements are queued in a durable outbox (`atlas_placement_outbox`) in the system
catalog, after the create, move-home (`CompleteHomeMoveAsync`, `FollowHomeLeaseAsync`)
and delete commits. Deletion queues the tombstone before any cleanup, including TTL
expiry; forgetting a copy homed elsewhere does not queue a tombstone. Create releases
its quota lease before queueing. These hooks never throw: queueing uses its own 10-second
timeout, independent of request cancellation. Any exception or unavailable catalog
write is logged at Error with project and database and counted as `queue_failed`.
A background publisher scans every outbox page, selects up to 64 due rows ordered by
`next_at`, and POSTs them every 2 seconds to `<AtlasUrl>/v1/publish`, signed with ES256
(`X-Atlas-Timestamp`, `X-Atlas-Signature` base64url r||s over
`atlas-publish-v1\n<timestamp>\n<body>`). Atlas answers `updated` or `stale` per item;
both mean done and the row is deleted (unless a newer queue replaced it). Any other 4xx
except 408/429 can never land: it is logged once at Error and dropped. 408, 429, 5xx and
network errors retry with exponential backoff (1 s doubling to 60 s).

## Resync

Every 10 minutes each region re-queues every database homed in that region with its
home, so Atlas converges even after missed events, cross-region timestamp ties or its
own data loss. For each database in the page, resync re-reads the current catalog
record inside the queueing write transaction. It skips databases that were deleted or
are no longer homed here, and queues the current name and home with a fresh timestamp.

## Settings

Both or neither; one alone fails startup with
`TERRABASE_ATLAS_URL and TERRABASE_ATLAS_PUBLISH_KEY_FILE must be set together`:

- `TERRABASE_ATLAS_URL`: absolute https URL (http allowed on loopback).
- `TERRABASE_ATLAS_PUBLISH_KEY_FILE`: path to a PKCS#8 PEM P-256 private key.
  Startup exports the private parameters and verifies the P-256 curve OID; public-only,
  unreadable and other-curve keys fail with the configuration error naming this setting.

With neither set, nothing is registered and TerraBase behaves exactly as without this
feature.

## Metrics

- `terrabase.atlas.placement_publishes` (counter, by `result`: updated, stale,
  retryable, refused, queue_failed).
- `terrabase.atlas.placement_pending` (gauge): rows waiting in the outbox.

## Routing safety

The published region is a database holder, not a project default. Region-set placement
keeps the requested set, replica membership and placement version when moving its
writer. Atlas is a routing hint: TerraBase still checks the database placement on
each request and refuses a region that no longer holds it, including requests with a
stale Atlas pin. All regions use the same placement checks.
