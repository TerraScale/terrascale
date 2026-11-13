# Atlas placements

TerraBase tells [Atlas](../../region-identity.md) (the Cloudflare Worker that routes SDK
clients) which region homes each database. TerraBase is the source of truth; Atlas only
keeps what it is told.

## What is published

A placement per database: `{"v":1,"project","database","region","updated"}`. `region` is
the database's home region id (`ManagedDatabase.HomeRegion`, or the local region while
null); `region: null` means the database was deleted. `updated` is unix seconds and must
be strictly increasing per (project, database) as Atlas sees it, so a re-queued row uses
`max(now, previous queued + 1)`.

Placements are queued in a durable outbox (`atlas_placement_outbox`) in the system
catalog, in the same write path as the create, move-home (`CompleteHomeMoveAsync`,
`FollowHomeLeaseAsync`) and delete commits. A background publisher batches up to 64 due
rows every 2 seconds and POSTs them to `<AtlasUrl>/v1/publish`, signed with ES256
(`X-Atlas-Timestamp`, `X-Atlas-Signature` base64url r||s over
`atlas-publish-v1\n<timestamp>\n<body>`). Atlas answers `updated` or `stale` per item;
both mean done and the row is deleted (unless a newer queue replaced it). Any other 4xx
except 408/429 can never land: it is logged once at Error and dropped. 408, 429, 5xx and
network errors retry with exponential backoff (1 s doubling to 60 s).

## Resync

Every 10 minutes each region re-queues every database homed in that region with its
home, so Atlas converges even after missed events or its own data loss.

## Settings

Both or neither; one alone fails startup with
`TERRABASE_ATLAS_URL and TERRABASE_ATLAS_PUBLISH_KEY_FILE must be set together`:

- `TERRABASE_ATLAS_URL`: absolute https URL (http allowed on loopback).
- `TERRABASE_ATLAS_PUBLISH_KEY_FILE`: path to a PKCS#8 PEM P-256 private key.

With neither set, nothing is registered and TerraBase behaves exactly as without this
feature.

## Metrics

- `terrabase.atlas.placement_publishes` (counter, by `result`: updated, stale,
  retryable, refused).
- `terrabase.atlas.placement_pending` (gauge): rows waiting in the outbox.
