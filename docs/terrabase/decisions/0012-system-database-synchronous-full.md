# Durable database commits

The system database stores catalog registrations, authorization-related state and revocation witnesses. Losing an acknowledged change can restore authority that was revoked. Its acknowledged writes must therefore cross the durable filesystem boundary rather than remaining only in the OS page cache.

Every database write transaction, including system and customer SQL writes, uses the engine's durable commit path. Acknowledgement follows the required WAL sync. Process, OS and power-loss qualification must verify the exact pinned native runtime and storage stack; this policy is not evidence that an arbitrary volume survives loss.

A lost or corrupt volume still requires authenticated backup recovery and catalog authority. Regional replication and home acceptance have separate guarantees; see [home apply](../home-apply.md), [write routing](../sql/write-routing.md), [copy faults](../replica-rebuild.md) and [continuous backup](../backup.md). A routing preference or an unconfirmed regional copy is not a substitute for a durable acknowledgement.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
