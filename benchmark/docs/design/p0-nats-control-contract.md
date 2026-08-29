# P0 contract: NATS control-plane subjects and boundaries

Status: frozen by the P0 contract pass, 2026-08-28. Governs P5
implementation. Parents: ADR 0014 D-4, ADR 0012 (NATS role), charter
PD-2026-08-28-03. Extends `crates/terrabase-member-transport/src/nats_usage.rs`
(KV/config/discovery audit) with the typed request/reply surface.

## 1. What may ride NATS (closed list)

NATS serves exactly: KV state, configuration sharing, discoverability,
and typed bounded request/reply for cluster control. It NEVER carries
documents, CRDT operations, WAL records, replication frames, sync
payloads, checkpoints, tier-manifest object bytes, or mover receipt
payloads. Engine-owned authenticated QUIC links carry all database
data. Tests assert the no-data rule (payload-shape allowlist on every
subject).

## 2. Subject map (frozen; `$CLUSTER` = cluster identity slug)

### KV buckets (JetStream KV, CAS-revision fenced)

| Bucket | Key shape | Value (bounded, typed) |
| --- | --- |
| `terrabase.$CLUSTER.members` | `member/<id>` | lease: member id, region, endpoints, capabilities, config_epoch, expires_at, revision |
| `terrabase.$CLUSTER.topology` | `snapshot` | last accepted topology digest + config_epoch (full object via request/reply) |
| `terrabase.$CLUSTER.config` | `epoch/<n>` | accepted configuration epoch record (replica sets, Regions, shard map digest) |
| `terrabase.$CLUSTER.mode` | `identity` | immutable cluster identity: mode active/backup, creation record, identity hash |
| `terrabase.$CLUSTER.routes` | `region/<r>` | route advertisements: eligible members per region |
| `terrabase.$CLUSTER.tierpolicy` | `tenant/<t>` | tier-policy version pointers (policy bodies live in engine storage) |

- KV writes use compare-and-swap on revision; a stale Member loses the
  CAS and re-reads. KV values are bounded (default 64 KiB) and
  schema-validated.
- A KV lease expiring NEVER authorizes eviction, data loss, or
  ownership transfer (ADR 0014 D-4). It only marks the Member's
  advertisement stale.

### Request/reply subjects (typed, bounded, timeout-bounded)

| Subject | Request | Reply |
| --- | --- | --- |
| `terrabase.$CLUSTER.ctrl.join` | join token, member identity, capabilities | accept/reject + current config_epoch + topology snapshot ref |
| `terrabase.$CLUSTER.ctrl.health` | member id | health digest |
| `terrabase.$CLUSTER.ctrl.topology` | config_epoch known | full topology snapshot if newer |
| `terrabase.$CLUSTER.ctrl.config_refresh` | config_epoch known | config record if newer |
| `terrabase.$CLUSTER.ctrl.route_lookup` | shard id, region | eligible member endpoints (location only — data never follows) |

- Every subject has a declared request/reply schema, a bounded payload,
  and a bounded timeout; unknown subjects are ignored; malformed
  payloads are dropped and counted, never fatal.
- NATS locate-vs-transport rule: a reply may name WHERE data lives
  (endpoints, object keys) but never the data itself.

## 3. Authority rules

- NATS advertises accepted control metadata; it cannot grant a vote,
  change a write quorum, appoint a leader (there is none — ADR 0018),
  or mutate a cluster's immutable mode.
- A Member acts on a configuration epoch only after durable local
  verification of the quorum-accepted record (KV is a cache of that
  decision, not the decision).
- Loss of NATS: new joins, topology changes, and configuration changes
  pause (fail closed). Already admitted Members continue serving,
  converging, and fulfilling last-resort reads over their last verified
  QUIC topology. Recovery receipts must show no data-plane dependence
  on NATS availability.

## 4. Bootstrap

A new Member starts with a seed NATS URL plus a cluster join token (or
equivalent bootstrap identity), calls `ctrl.join`, and receives the
current epoch + topology + member endpoints; it then establishes
authenticated QUIC data links directly with peers. Cluster creation
declares `active` or `backup` mode in the identity record at that
moment (ADR 0017 D-4).

## 5. Honest limits

The current integration covers KV/config/discovery audit only
(`nats_usage.rs`); the typed request/reply surface, CAS-fenced buckets,
and join flow are `unresolved` until P5 delivers them. No nats-server
runs on this host today — live-server tests are recorded `unrun` when
the server is absent (never fabricated).
