# Node identity in local backup object keys

After removal of leaderless replication, every serving node ships its own local files. Equal organization, project and database identifiers do not establish shared backup history: system catalogs and platform databases can contain different data on independent nodes.

The file roots are now:

- Database: `v1/<organization>/<project>/<database>/nodes/<TS_NODE>`.
- System catalog: `v1/_system/nodes/<TS_NODE>`.

Each root contains its own `snapshots/`, `wal/<generation>/` and `generations/<generation>` objects. `TerraBaseOptions.MachineId`, derived from ts-orch's `TS_NODE`, supplies the stable validated path component. Node identity must remain unique among independent serving nodes and stable across restarts. A replacement operating under the same node identity selects that identity's history. There is no backup partition setting or source-node override.

Shipping, progress lookup, `/v1/backup` file names, restore windows and plans, historical reads, restore replay, drills, size accounting and retention all use the local node's partition. Timestamp ordering and retention predecessors apply within that history. System-catalog rebuilds retain their restriction to snapshot times, including offline reads of another node's partition. History-erasure verification checks the node partition associated with each holder receipt and its authenticated rewrite lineage.

Project key scopes remain `v1/keys/<organization>/<project>/`; system keys remain `v1/keys/_system/`. Independent nodes may use the same wrapped project key. Backup authentication binds ciphertext to its complete object key, including the node partition. Keys are not deleted by ordinary per-file retention. Project crypto-erasure still covers every node through the project data and key prefixes, and database purge still inventories the parent database prefix across every node. Customer destination probes and `BackupTargets` continue to enforce the existing organization/project data and key boundaries; adding the suffix grants no access outside those boundaries.

This is a greenfield object-key compatibility break. Unpartitioned histories are neither selected nor migrated, and there is no fallback to the earlier keys. Re-bootstrap the backup inventory as part of the pivot reset. Snapshot bundles, log encoding, key encoding and Turso storage format are unchanged; `StorageFormat` remains 19. Tools that audit a single file must pass the complete node-qualified root.

`BackupNodeIsolationTests` covers platform-only, customer-only and mirrored destinations using separate real hosts, listeners and Turso directories with equal database IDs and different stable node identities. It writes divergent snapshots and log updates, restores each history through HTTP, checks drills and progress, and verifies one node's retention expires its own baseline while preserving the other node's objects and restore.
