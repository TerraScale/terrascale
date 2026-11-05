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
