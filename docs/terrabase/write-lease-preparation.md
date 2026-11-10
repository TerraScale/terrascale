# Initial home lease preparation

Creating a replicated database, or adding replicas to one, confirms its home
lease before the successful management response. This includes all-regions
placement. Home-apply tables are prepared only after that confirmation.

Preparation uses the same serialized confirmation and JetStream compare-and-set
as the lease scan. A node without a ready lease session, or whose confirmation
loses to another home, refuses preparation. No write may bypass the engine's
existing lease checks at transaction begin and commit. Lease expiry, renewal
margin and failover fencing remain unchanged.

The first confirmation previously happened on the 15-second catalog scan.
That scan closed the write gate while it acquired the lease over the WAN,
interrupting writes that were already active. Moving this work into management
preparation makes the initial lease acquisition part of database setup.
Subsequent scans reuse the confirmed session without closing the gate.

Regional-only databases, including auth and dashboard's independent regional
databases, use the regional writer's CAS key in
`terrabase-region-owners/<region>`. They do not use global per-database
`terrabase-homes/home.<database-id>` keys. Platform database IDs are shared
between regions, so their global lease records must be ignored even before the
local catalog is available. Known regional-only placements are also excluded
from global lease observation, expiration, takeover and demotion. R10's regional
acknowledgement holds are independent and remain intact.

Legacy global platform keys must remain until all watchers run this isolation
code. Removing those keys is separate from deploying the code and requires a
placement/data audit; deleting keys alone does not make a watcher restart safe.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
