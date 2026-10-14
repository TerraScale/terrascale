# 8. Transport

- **Peers.** Phase 3a's `FlyMachines` directory: TXT `vms.ts-terrabase.internal`, refreshed every 5 s. A database's
  peers are the machines whose region is in its placement (section 16), minus self. They are addressed per machine at
  `http://<machine>.vm.ts-terrabase.internal:8081`: the internal listener on `fly-local-6pn` (h2c, gRPC). Region names
  like `<region>.ts-terrabase.internal` serve routing only, because a region will hold more than one machine. There is
  no mTLS. Every internal request carries `X-TerraBase-Peer-Credential` from the shared Fly secret `TERRABASE_PEER_SECRET` (see [peer-authentication.md](../peer-authentication.md)); the old `PeerCredentials` and `peer-*` settings remain removed.
- **Protocol** (replaces `terrabase/v1/replication.proto`, `catalog.proto`, `wan.proto` and
  `terrabase/alpha/anti_entropy.proto`; package `terrabase.v1`, everything v1):

```proto
service Replication {
  rpc Push(stream PushFrame) returns (stream AckFrame);        // one per (sender, receiver) machine pair
  rpc Digest(DigestRequest) returns (DigestReply);             // R6: 4096 bucket digests of one database
  rpc ReadBucket(BucketRequest) returns (stream StatePage);    // R6: states of one bucket, paged
}
message PushFrame { oneof kind { Hello hello = 1; Batch batch = 2; Heartbeat heartbeat = 3; } }
message Hello { fixed64 node = 1; fixed64 hlc = 2; string region = 3; } // origin of every group on the stream
message Database { string organization_id = 1; string project_id = 2; string database_id = 3; }
message Batch { Database database = 1; uint64 last_change_id = 2;
                bytes groups = 3; }                             // LZ4 envelope (decision 0007) of a GroupList
message Heartbeat { fixed64 hlc = 1; }                          // every 1 s when idle
message GroupList { repeated Group groups = 1; }
message Group { uint64 change_id = 1; repeated Delta deltas = 2; }
message Delta { string collection = 1; string id = 2; fixed64 stamp = 3; bytes state = 4; } // full-form DocumentState
message AckFrame { oneof kind { Welcome welcome = 1; Ack ack = 2; } }
message Welcome { fixed64 node = 1; }                           // the receiver's node: the sender's cursor key
message Ack { Database database = 1; uint64 change_id = 2; fixed64 applied = 3; bool refused = 4; }
```

As built in R4a (`Features/Replication/Protos/terrabase/v1/replication.proto`): a database is named by its three
ULIDs, because storage is addressed by tenant. The **sender owns the cursor**: one `_tb_peers` row per node serves
both directions of a pair, so the receiver cannot also keep the sender's change ids there. The receiver answers the
`Hello` with a `Welcome` carrying its node. The sender then resumes each database from that peer's `acked` and records
every `Ack` back. `refused` means the receiver does not hold the database yet (its catalog lags), and the sender
retries 5 s later. R6 adds `Digest`, `ReadBucket` and `refill_required`; R7 adds `stable`.

An accepted `Push` publishes its gRPC response headers before waiting for a predecessor's refill to end. The
`Welcome` still waits for that handover; host shutdown can therefore finish the accepted response without a
`Welcome`, and the sender reads the end of the stream rather than an unstarted response becoming gRPC Internal.

- **Sender.** One `Push` stream per peer machine carries every database the pair shares. It holds at most one batch
  (≤ 1024 groups, ≤ 2 MiB) in flight per database and resumes from the receiver's acked `change_id` after a
  reconnect. The `ReplicationSender` of PR #11 takes its peers once at start. The new sender follows the directory
  continuously.
- **Liveness.** HTTP/2 keepalive pings run after 10 s with a 5 s reply timeout. Independently, a `Push` sender must
  receive a response within 15 s, including while a write is blocked. The receiver requires another frame within
  15 s, starting before `Hello`, and aborts the request when that deadline expires so a blocked response write ends.
  The sender emits a heartbeat every idle second; the receiver answers it with an empty `AckFrame`. That response
  advances only the liveness deadline, never a durable ACK or stable stamp. A live refill keeps exchanging these
  heartbeats while its per-database ACK retains the existing 10-minute allowance. A failed stream reconnects from
  the last durable ACK with the existing 1–30 s backoff.
  On the sender, heartbeats and response reads run independently of local database work, with serialized writes and at most 32
  response-producing frames awaiting local processing. A matching batch/refill response satisfies its peer progress
  deadline on receipt; the durable cursor advances only when the sender processes that response. Progress deadlines
  remain active while another database's local read waits. A frontier blocked by an unfinished refill receives a zero
  stable report to release its response slot; the refill block remains until repair completes. The receiver processes ordinary batches and frontier reports
  in order; that local work must finish within 15 s so it can continue reading heartbeats. A longer wait ends the stream
  and retries from the durable ACK. Refills run separately and keep their 10-minute allowance.
