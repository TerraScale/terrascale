# Readiness probe durability

`/readyz` is served from a background check (`ReadinessProbe`, `SigningKeyReadiness`, `TerraBaseStorageHealth`). Among
other steps, the check creates and deletes one synthetic document in `storage_health`. These writes now carry no
`TB-Durability` header (local_durable), like rate-limit buckets ([rate-limit-durability.md](rate-limit-durability.md)).
Every identity write keeps `remote_durable`.

## Why

Readiness answers whether this replica can serve with this region's storage. It does not measure replication lag. With
`remote_durable`, the probe's create and delete each waited for another region's acknowledgement. On 2026-10-27, gru's
TerraBase was replicating slowly: a departed peer identity had pinned a 714k-row CDC backlog, fixed by terrascale #760.
The probe then passed its 8 s deadline every 10–40 s, and `/readyz` flapped between 200 and 503 from 14:06Z while sign-in
pages still answered.

## Trade-off

The synthetic document never matters after the probe: it is deleted in the same pass, and losing it with its region loses
nothing. Replication health is reported by TerraBase's own `/health/replication` and alerts. Slow cross-region acks still
show up where they matter: as `remote_durable` identity-write latency.
