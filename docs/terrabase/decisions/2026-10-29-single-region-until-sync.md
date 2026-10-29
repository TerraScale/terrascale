# Single-region storage after E5

The SQL pivot removes TerraBase’s leaderless replication engine. Each database has one serving region until R1–R3 supply the replacement replica implementation. A successful local write confirms the local commit; no peer acknowledgement or remote durability is available.

Peer streams, remote apply, lattice joins, anti-entropy, repair, refill, replica rebuild and their administrative status APIs are removed. The old replication metrics, peer-state tables and replicated test scenarios are removed with them. Region discovery still reads the ts-orch peers file for routing and private listener binding; it does not create database copies.

Shapes retains its local committed feed, persisted sequence, prefix hashes, retention, sealing, snapshot and resume paths. F1 will replace the source. Shape identities bind to the local file incarnation, so copying or replacing a file invalidates old handles.

Continuous backup still reads the independent CDC log, encrypts snapshots and log segments, and uploads them to Tigris. Every local file is shipped by its local node. E8 owns replacement of backup capture for native SQL tables.

Catalog authorization still requires local restore admission. An encrypted external anchor names the local system file incarnation and committed offset. Existing catalogs with a missing, unreadable or incompatible anchor, a different incarnation or an anchor newer than their local offset stay closed. Successful revocations and destructive changes must write their anchor before acknowledgement. The local anchor is version 2; no compatibility path accepts the former replicated anchor format.

The storage format is bumped to 19 because replicated state and repair metadata are removed. Old data directories are intentionally refused; the pivot uses a clean bootstrap rather than a migration. This change does not add an authentication bypass, relax API-key revalidation or allow cross-tenant shape key access.
