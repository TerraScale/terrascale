# 0007: Replication over the LZ4 application envelope

- Status: proposed (2026-10-04); supersedes 0001; the merge policy below needs human approval
- Scope: `Features/Replication`, `Engine`

## Context

Ticket TS-464 requires LZ4 as a versioned application envelope (lengths, checksums, bounded
decompression), not as native gRPC compression. This matches terrabase-rust
`docs/distributed-protocol.md`, "gRPC transport and bounded framing". 0001 had chosen a custom
`lz4` `grpc-encoding` and left the choice open until `CommittedGroupBatch` landed.

## Decision

### Wire format

- `Lz4CompressionProvider` (`grpc-encoding: lz4`) is removed; gRPC transport compression is not
  used. Every change group travels in the terrabase-rust `TBLZ4E01` envelope
  (`tb-format/src/envelope.rs`) with the same byte layout (`ApplicationEnvelope.cs`): a 40-byte
  little-endian header (magic, major 1, zero flags, codec byte 1 = LZ4, total, stored and decoded
  lengths, CRC32C of header bytes 0..28, of the stored block and of the decoded bytes), then one
  raw LZ4 block (NativeCompressions `LZ4.Block`).
- Only the LZ4 codec is written or accepted (`PAYLOAD_CODEC_LZ4_BLOCK_V1`); Identity is never
  negotiated. Decoding checks the lengths and the header and block CRCs before allocating exactly
  the declared size (at most 4 MiB), decompresses into that bounded buffer, then checks the
  decoded length and CRC.
- Control frames (`Hello`, `Welcome`, acknowledgments, errors) are small uncompressed Protobuf.
  The repository rule "inter-node gRPC uses LZ4" is met by the payload envelope.
- The plaintext is a C# change group, `TBDCG001` (`ChangeGroup.cs`, little-endian): magic,
  `count:u32`, then per change `kind:u8` (0 put, 1 delete), collection (`u16` length, UTF-8), id
  (`u16` length, UTF-8), `version:u64` and, for a put, the JSON (`u32` length, UTF-8). It is not
  the Rust `TBWAL001` WAL frame, so C# and Rust peers do not interoperate: a Rust frame fails the
  magic check.

### Capture

- Every committed write transaction on a client database (never `_system`), whether or not
  `peer-addresses` is set, appends its puts and deletes to a `changes` table in the same SQLite
  transaction, numbered 1, 2, … under the database's existing `commit_offset.incarnation`: a
  random UUID created with the file (document API schema v4), exposed through
  `IDatabaseStore.Incarnation`. The wire origin is its 16 bytes in RFC order. Schema v7 adds the
  journal and applied-prefix tables without a second identity.
- A captured transaction holds at most 1024 final document states and 2 MiB of UTF-8 collection, id and JSON
  bytes, checked before copying or replacing each final state; a larger one fails and its transaction cannot
  commit. The receiver enforces the same limits on every group.
- The `replication_state` row records whether the log covers the database's whole state. A copy
  or a populated database upgraded from a schema without a journal clears it; the node then appends the current state of every document
  and tombstone as baseline groups (bounded, one per transaction) when it first opens the
  database, and sets it again. Nothing truncates the log, so no group a peer may still need is
  dropped.
- A pre-existing document larger than 2 MiB of group content is logged and omitted from the
  baseline; it remains readable and deletable locally, and other documents still replicate.
- Local document versions are database commit offsets shared by every mutation in a transaction.
  The offset advances to at least every admitted imported version; local allocation stops at
  `long.MaxValue - 2^32`. Exhaustion fails and faults the transaction. Only the final state of each
  document in a local transaction is captured, so an intermediate equal-offset tombstone or
  body cannot win over the final state on a receiver. Physical deletes and collection drops
  capture versioned document deletions as well. An indexed lookup of retained journal deletions
  rejects older or equal incoming states even after a physical removal. Collection metadata
  remains local; a received
  change supplies a default collection when necessary.
- Incoming winners append shape history in the same transaction as their applied frontier.
  Schema v7 separates the original merge version from the local shape history offset. Each
  import transaction advances history above the previous local head. Document ETags include the
  merge version, raw body and deletion state, so equal-version winners invalidate old validators;
  clock-only progress also wakes live readers. Imported changes are not
  recaptured into the sending journal.
- Change notifications run after the commit; a subscriber that fails is logged and does not fail
  the committed write.
- Local document mutation calls and replicated applies cannot share a transaction in either
  order, including calls that change nothing. A rejected mix leaves the transaction rollback-only.
  Collection creation and applied-frontier updates remain available to the receiver.
- Copies materialize retained physical-deletion evidence as destination tombstones before
  resetting the journal, preserving merge ordering and including those deletions in the new baseline.

### Incoming clock admission

On 2026-10-04 the user authorized choosing the version policy for review finding 14893.
The selected policy retains the signed 64-bit persisted and wire representation and merge
ordering, with these shared limits (`Engine/VersionClock.cs`):

- Each imported version is positive and at most `long.MaxValue - 2^32`.
- One group may advance its database clock by at most `2^32` from the head read at the
  start of its write transaction. The complete group is checked before any mutation;
  direct engine imports use the same fixed starting head for every Apply call.
- Local merge-version allocation stops at the terminal version. Imported winners and actual
  collection metadata changes may allocate distinct shape history offsets in the reserved tail
  up to physical `Int64.MaxValue`, preserving their admitted original merge versions. A rejected Apply faults its transaction, so catching it cannot commit partial
  document, history, clock or frontier changes. No incoming merge version is clamped.

This prevents an extreme peer version, including a losing state, from exhausting a healthy
clock. It changes admission for legitimate catch-up gaps above `2^32` and legacy versions
above the terminal limit: those groups receive non-retryable `InvalidBatch` and remain
unacknowledged. The configured sender will retry but cannot close such a gap automatically;
operators must stop that replication attempt. A matching-state snapshot bootstrap with a new
incarnation can recover a legitimate gap whose versions remain within the terminal limit.
Copies preserve versions: they do not repair a clock at or above terminal capacity. Those
files require a wider future format or a separately approved version rewrite. No automated
recovery operation is provided by this PR. Existing exhausted legacy files remain readable
and refuse further local writes.
The reserved numeric range is usable only for imported history, not local document versions;
reaching the terminal merge version still requires recovery for local writes. A wider future version format is the upgrade path if these limits
become operationally relevant.

### Streams

- One `Sync` stream per (peer, organization, project, database). `Hello` carries all three
  tenant-qualified UUIDs as 16 bytes in RFC order; `project_id` is an additive field. Each
  event and acknowledgment carries the same full identity, validated against the handshake.
  `Hello` carries the origin bytes in `member_id` and `member_incarnation`; there
  is no node-level member id until membership exists (TS-469).
- `Hello` and `Welcome` also carry the configured node region in the additive `PeerIdentity.region`
  field. Nonempty values use `TERRABASE_REGION` syntax (at most 63 characters); older peers may omit
  it. Both sides log the regions after validation. Region is descriptive, outside journal identity.
- The receiver opens (or creates) the database on `Hello`. `Welcome` names the receiving database
  as a member (its own origin bytes) and returns its applied prefix of the sender's origin and the
  negotiated limits (the minimum of each); the sender resumes after the prefix.
- Creation addresses only the exact organization, project and database IDs in that identity.
  Management catalog entries remain local and must be provisioned with those same IDs on every
  node before client routes can resolve the replicated file. Tenant names never identify peer storage.
- Until membership (TS-469) every node is in epoch 0 with an empty configuration id. `Hello`,
  `Welcome`, batches and acknowledgments carry it; another epoch gets `STREAM_ERROR_STALE_EPOCH` or
  an invalid batch.
- Before releasing anything, the sender checks every `DurableAck`: the acknowledging peer from
  `Welcome`, the epoch, its database and origin, and a prefix between the last one and the last
  group sent. It fails closed on a `Welcome` that selects anything but protocol 1.0, format and
  merge policy 1 and LZ4, or whose limits are not exactly the minimum of both sides.
- One limit check (`ReplicationProtocol.CheckLimits`) runs on the sender before it writes a batch
  and on the receiver before it decodes one: gRPC message size, encoded and decoded bytes, changes
  per group, byte credit and group content bytes, all as negotiated.
- The sender's durable lag (committed groups not yet durably acknowledged by a peer) is a gauge;
  the integration tests use it as their replication barrier.
- A turn captures its head in one short read, then reads each bounded group in a separate short
  transaction that ends before compression and network I/O. It sends at most 256 groups.
- Every 5 s known client databases are queued for frontier reconciliation, with pending work
  coalesced by database incarnation. A lost receiver prefix is discovered without new local writes.
- Sender state is retired on database deletion. Receiver streams pin their store, cancel on
  retirement and require a new Welcome for a replacement. Duplicate acknowledgments consult
  the pinned persisted frontier rather than relying on a cached stream prefix.
- Read transactions enable SQLite `query_only`; pooled sessions reset it after reads end.
- Full mesh: each node sends only the groups it originated. Applied groups are never captured.
- A sender whose log ends below the peer prefix or whose cumulative digest differs sends
  nothing for that stream. The receiver rejects different content at its current applied prefix;
  older duplicates change nothing and receive the persisted current prefix and proof.

### Receiver

- Validates the protocol, format, merge policy and codec, the identity, the declared sizes, the
  envelope and the change group: collection names (database-name rules), ids of 1 to 256
  characters without control characters, versions ≥ 1, JSON objects of strictly valid UTF-8.
  Storage uses the same admission path for local writes and incoming Apply: a complete UTF-8
  object, no duplicate properties, at most 1 MiB, 16 container levels and 4096 values, with the
  document API numeric limits. Invalid incoming states, including losing states, fault the
  transaction and do not advance the applied frontier. Legacy incompatible values require
  operator recovery into a fresh database; they are not normalized automatically.
- Applies the group and records its sequence in the `applied` table in **one** SQLite
  transaction. `DurableAck` and `AppliedAck` are sent after that commit.
- Sequence ≤ prefix: duplicate, acknowledged, not applied. Sequence > prefix + 1: gap, retryable
  `StreamError` (`STREAM_ERROR_INVALID_BATCH`); the sender resumes from the next `Welcome`.
- Invalid batch: non-retryable `STREAM_ERROR_INVALID_BATCH`; unsupported protocol, format or
  codec: non-retryable `STREAM_ERROR_UNSUPPORTED_FORMAT`. Nothing is applied either way.
- A database is created on the receiver when a sender first opens a stream for it.

### Merge policy v1 (needs approval)

- Deterministic last-writer-wins per document, ordered by (version, tombstone over live
  document, UTF-8 JSON bytes). No wall clock is used.
- Local commit offsets advance after every imported version or tombstone
  (`documents.body = NULL`), so a write that observed a state supersedes it. Two concurrent writes at the same version both
  converge to the greater one; the other value is discarded.
- This is not the Rust causal multi-value register (siblings); porting it is follow-up work.

### Limits

- gRPC message ≤ 4 MiB + 64 KiB; envelope and decoded group ≤ 4 MiB each.
- Per stream, ≤ 8 batches and 16 MiB of envelopes in flight, lowered to the peer's limits; a
  `DurableAck` releases the groups it covers. A group that does not fit the negotiated limits
  fails the stream.
- ≤ 16 open `Sync` streams per node; more get retryable `STREAM_ERROR_RESOURCE_EXHAUSTED`.
- Receiver idle timeout 30 s, for the wait for `Hello`, request reads and response writes (TLS
  handshakes have Kestrel's 10 s limit). Sender: 15 s to connect, 15 s for `Welcome`, then 15 s
  between acknowledgments that advance the durable prefix, extended by nothing else. Retry backoff
  100 ms doubling to 10 s with full jitter.

### Mutual TLS

- `peer-certificate` is both served by the peer listener and presented by the sender, so it needs
  the server and client authentication EKUs. Peers must chain to a certificate in `peer-client-ca`;
  every certificate in that PEM file is trusted, so a CA rotation can overlap old and new CAs.
  The leaf-first certificate PEM also carries bounded intermediates. Offline TLS contexts
  present that chain in both directions; supplied intermediates are never added as trust roots.
  CustomRootTrust uses only configured CAs, with revocation and issuer downloads disabled.
- The certificate must chain to `peer-client-ca` for both usages at startup (else exit code 2).
- The three PEM files are checked at most once per monotonic second and reloaded when their
  SHA-256 content fingerprints change (no restart). One snapshot supplies both the certificate and trust authorities throughout
  each handshake. Until files can be read consistently and the certificate chains to the new
  authorities, the previous pair stays.

## Consequences

- Not interoperable with terrabase-rust.
- `changes` and document tombstones are never truncated; they need retention once membership knows all
  peers.
- Database delete is not replicated. A copy gets a new incarnation and empty `changes` and `applied`
  tables, so it never continues its source's numbering (a peer could otherwise skip its groups as
  duplicates); its content reaches peers as a baseline.
- `_system` (API keys, management catalog) is not replicated.
- A baseline costs as much log space as the data it covers.
- `DurableAck` follows the receiver's SQLite commit, which [0010](0010-write-wait-and-synced-durable-ack.md) syncs to
  disk (`synchronous = FULL` for replicated applies), as the terrabase-rust contract requires.
- No SQL write path exists yet. A future SQL write through
  `IStorageTransaction` is captured as one group.

### Journal prefix proof (schema 8)

Welcome, committed batches and durable/applied acknowledgments carry a mandatory 32-byte
chained SHA-256 journal identity. The empty prefix hashes `TerraBase/journal-prefix/v1\0`
and the 16 RFC-order origin bytes. A group hashes `TerraBase/journal-group/v1\0`, the origin,
previous digest, Int64 sequence, Int32 change count, then each ordered change's length-framed
strict UTF-8 collection/id, Int64 merge version and Int32 body length followed by body bytes.
Integers are little-endian; body length -1 denotes a deletion. The digest binds the complete
ordered prefix, not only its sequence.

Schema 8 persists local prefix digests with journal appends and imported digest/frontier pairs
with their merge transaction. Migration backfills bounded contiguous local groups and clears
legacy incoming frontiers lacking proof, causing safe replay. Existing incarnations remain
stable on ordinary reopen. A restored sender compares the peer's proof with its local prefix
before trusting Welcome or releasing any ACK credit. Different content at reused sequences
requires explicit recovery; it sends no replacement groups and does not report durable lag zero.
An operator can create a fresh-incarnation copy with a baseline and retire the ambiguous source.
All peers must upgrade together; earlier protocol-1 peers lacking these fields fail closed.

Every serialized Sync response obeys the negotiated gRPC cap. A cap too small for the largest
required Welcome/ACK is refused with ResourceExhausted trailers; oversized control responses
also terminate with trailers rather than sending an error frame beyond the cap.

Peer credentials check content at most once per monotonic second, with one nonblocking reload
attempt; concurrent connections use the current pair. PEM reads are bounded to 1 MiB each and
64 trusted CAs. A connection lease pins its leaf and trust set until close; replaced generations
and the active host generation dispose their native handles after their final lease is released.
