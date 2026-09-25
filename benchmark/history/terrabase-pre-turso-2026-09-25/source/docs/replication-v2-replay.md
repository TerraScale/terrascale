# Replication v2 verification and exact inbox replay brief

Status: implementation brief only. This does not enable document apply or an
`AppliedAck`. The accepted physical and ordering contract is
[document-state-v2.md](document-state-v2.md); that document controls if this
brief and the codec disagree. The first implementation packet is the record
format and same-frame semantic proof. The journal replay packet follows the
format coverage decision and the storage prepared-commit handoff.

## Pinned source and ownership

At TerraBase HEAD `352d9f61185d1b685e3032519579040d169ad47d`, the
uncommitted working-tree SHA-256 identities observed for this brief are:

| Path | SHA-256 |
| --- | --- |
| `docs/document-state-v2.md` | `22ef94c55efa856422b8ec033192ccbd68c8cc122c20447e6878dc4ad4965992` |
| `crates/tb-format/src/document_v2.rs` | `9c90785ddefca3d73bd5c00dcd433b922190d1aef4f831d62363966b0deaad9b` |
| `crates/tb-format/src/lib.rs` | `8b1550b0fbfe3df479c597cec8d6ccc1e865a63f70e823dd31bc5cfb93a5daee` |
| `crates/tb-replication/src/event.rs` | `9709a3bd59dff3c9532ad0ed595363f96e11a391c623738f0215e3af2caf542a` |
| `crates/tb-replication/src/metadata.rs` | `12d1997e1b465f5f4287bc4d33f9fe80bce9d92ef06c53a6625232dd47601412` |
| `crates/tb-replication/src/journal.rs` | `d080f85983d854d611f140026a1890f6ca701da83f0b82daaeb66c2fa114591c` |
| `crates/tb-replication/src/authority.rs` | `467a427836517297aaba2013842d231f650970230dba60f3bab82ff52b4cfb90` |

The replication owner changes only `crates/tb-replication/src/**` for these
packets. The format owner owns D0/D5/D6 codecs, the document owner owns source
production, pure-D6 staging/resend exclusion and document apply, the storage
owner owns the guarded `prepare_batch` seam, and the server owner composes the
real caller and ACK stages. Refresh hashes before work because the shared tree
can move. Do not edit the raw storage guard from the replication packet.

## Packet 1: record format 2 source proof

In `event.rs`, `EventVersions::validate` must accept exactly `record_format` 1
or 2 while retaining event format 1 and merge policy 1. In `metadata.rs`,
`validate_header` must make the same explicit choice. Unknown formats still
fail before a `CommitEvent` is admitted. Keep the exact `TBMET001` encoding and
the current event digest
`SHA-256("TBREPL-EVENT-V1\0" || u32_le(metadata length) || metadata ||
u32_le(WAL length) || complete WAL)` unchanged for both record formats.

`verify_event` already maps every complete CRC-checked WAL operation to one
metadata role. Preserve its format-1 role-4 path: decode the inline
`TBSTA001` D0, check its key/value identities, causal context and matching own
source sibling. For format 2, use `tb_format::decode_state_pointer(D0 key,
value)` to check the exact `TBPTR002` key/value, 80-byte length and CRC. Require
one primary D0 for each logical change, no duplicate primary, and a matching
same-frame role-2 D1 source change. The role-2 proof must still check the D1
key derived from collection/document, event origin/sequence and the **logical
sorted change-table ordinal**; `source_wal_ordinal` selects the physical WAL
operation only. `tb_format::decode_change` must confirm the complete D1 value,
EventId, collection/document, generation, schema digest and kind against the
metadata binding. For a live change it also checks its embedded document; a
tombstone has an empty body. The corresponding D0 pointer is only a checked
sender-local projection. Its D6 target cannot be dereferenced from this frame.
The source producer/reopen audit, which can access local D6, owns that
reference proof. The receiver never treats sender D0, D6, D3 retry records or
physical indexes as remote merge authority.

The current `CommitEvent` stores decoded document values and loses the full
D1 wrapper bytes. Add a proof-owned read-only exact-source view from
`VerifiedIncomingGroup`, with one entry per logical change: binding,
document/kind, logical ordinal, source WAL ordinal, and borrowed **full D1 key
and value** slices from the verified frame. One internal verifier should build
both `CommitEvent` and these bounded source views; keep `verify_event`'s public
behavior and use the richer result in `verify_incoming` instead of parsing a
second time. The semantic work permit must cover the views and remain alive
through the caller's journal sync or target preparation. A future receiver
copies the exact D1 key/value bytes into its final D1+D0+D5 group; decoded
`DocumentValue` is not a byte-preserving substitute. Never put D6 in `WalRole`
or accept a D6-only staging WAL as a distributed source event. The document
producer excludes pure-D6 groups from resend; the replication verifier rejects
one if presented as an event because it lacks the complete certified role
mapping and metadata marker.

Packet-1 tests in `metadata.rs` and `event.rs` should include existing v1
fixtures unchanged; valid v2 pointer with exact D1, bad pointer magic/length/
CRC/key identity, missing or duplicate own D1, wrong logical versus physical
ordinal, wrong generation/digest/kind, format 1 with v2 D0, format 2 with v1
D0, unknown format, and attempted D6 WAL role. A near-1-MiB legal full D1
value must survive verification and exact-source extraction without a second
re-encoding or an inline-state size restriction. Assert source/receiver frame
arithmetic at the 2-MiB and 1,024-operation edges, including the `2*n+1`
receiver operation count and copied D1 lengths from the accepted contract.
These tests establish semantic admissibility, not apply readiness.

## Packet 2: bounded exact journal read and replay authority

The private `ReplicaJournal::load_group` already reconstructs a canonical
`u32_le(metadata length) || metadata || u32_le(WAL length) || WAL` buffer from
marker-referenced chunks. It checks marker/chunk sizes and SHA-256, exact
lengths, event digest and WAL transaction identity, but it currently exposes
neither an owned replay value nor a public read path. Add a bounded private
read and a narrow `ReplicaReceiver::read_durable_exact(sequence, max_bytes) ->
Result<Option<OwnedDurableGroup>, ReceiveError>`. The receiver/journal already
retain the process `MemoryBudget`; charge that same budget before materializing
the output, with one byte-and-item RAII permit held by the result. `max_bytes`
covers both exact payloads and their two four-byte length prefixes. Reject
zero/oversize requests and compare the marker's checked total to `max_bytes`
**before** chunk allocation. `OwnedDurableGroup` has private fields, owns exact
metadata and WAL bytes plus marker EventId/digest/epoch/configuration and the
permit, and offers borrowing getters. An implementation may split the existing
canonical buffer or construct two vectors directly, but its peak temporary
memory must be admitted and the returned permit must cover retained capacity.

Derive the marker key from the receiver's pinned `CursorScope` and requested
nonzero sequence; never accept a caller-supplied key or origin. Return `None`
only when `sequence` exceeds the contiguous recovered durable prefix. A
missing marker at or below that prefix is corruption. Reject read while the
journal needs reopen after uncertain sync. Check marker key/identity/scope,
configuration, chunk indexes, hashes, exact lengths, canonical chunk sizing,
event digest and WAL transaction identity against one bounded read snapshot.
No read advances a cursor or produces an ACK. A higher out-of-order marker is
not eligible for this contiguous replay API.

Replay next chooses `sequence = contiguous recovered D5 applied prefix + 1`
for the same origin, and only reads if it is within the journal's durable
prefix. The journal's transient `OriginCursor::applied` field and a synced
inbox marker are not D5 authority. Add a **separate**, opaque
`CertifiedReplayEvent` admitted from this journal-owned exact group and a
fresh `verify_incoming` semantic proof. The view compares its private instance
token and immutable signed scope, marker identity/digest/epoch/configuration
against the proof, checks the origin/incarnation against a signed member row,
and checks every collection binding against the accepted catalog. This uses
the persisted operator-signed authority; it never calls `certify_peer`, which
requires a live TLS leaf, and it never manufactures `CertifiedMember` or a
live `CertifiedEvent`. The replay capability cannot be passed to
`append_certified`, and it has no applied-ACK constructor. Keep the owned
bytes, semantic permit and authority view alive until target preparation and
publication finish.

The document/server caller, outside replication ownership, compares or writes
the 156-byte D5 `TBAPL001` identity in the same target atomic group as exact
D1 and new D0 roots. It derives D5 from the verified EventId/digest and
accepted epoch/configuration, not sender D3 or a local commit sequence. On
reopen, exact existing D5 means skip reapply; a mismatch quarantines, and a
durable inbox marker with no D5 replays before serving writers. Applied prefix
comes from contiguous recovered D5 identities. Existing durable-only inbox
backlog must be reconciled before applied readiness. The storage owner will
provide a prepared final batch holding its storage-inner guard; architecture
confirmed the caller order is document writer gate, document storage-inner
guard, then inbox journal owner/storage, all within one bounded blocking task.
The replication packet does not alter this raw storage seam.

Packet-2 tests should cover exact v1 and v2 readback, smaller `max_bytes`,
zero/overflow sequence, absent marker above versus at/below prefix, synced
out-of-order marker, wrong scope/configuration, marker/chunk length/hash/
digest/transaction corruption, permit retention/release, and stale or missing
catalog/member on replay. Prove that a replay capability cannot satisfy the
live append API and that old v1 durable-only backlog can be reverified after
restart. The document/server integration gate then exercises D5 duplicate,
collision and gap, forced exits around D6 stage, inbox marker and final D5
sync, and two actual mTLS/LZ4 replicas. No `AppliedAck` or production
membership-readiness claim follows from either replication packet alone.

## Dependencies and remaining decisions

The format-only v2 codec is present at the pinned hashes above, with review
and guest execution still pending when this brief was written. Its outcome
must be fixed before packet 1 starts. Packet 2 depends on packet 1's exact D1
view and the accepted immutable authority; the real caller additionally
depends on the storage prepared-batch guard, the distributed document open,
D6 stage/resend audit and D5 recovery. The first slice retains all D1/D5/D6
and older referenced D0 values; reclamation, dynamic membership transitions,
and large-conflict resnapshot policy remain separate architecture work.
