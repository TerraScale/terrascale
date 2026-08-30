//! Golden vectors for the ADR 0023 native replication plane.
//!
//! Every test below pins the exact canonical bytes — or the
//! domain-separated BLAKE3 digest of canonical bytes — that the
//! replication core produces for one fixed input. Each pinned literal is
//! cross-checked against a JSON fixture under `tests/vectors/adr0023/`,
//! so a codec change that moves a single byte breaks both the literal
//! assertion and the fixture assertion.
//!
//! Pinned structures:
//!
//! - the member-to-member replication frame: the
//!   [`ReplicaExchange::Delta`] batch the native exchange codec
//!   (major version 1) carries, in full and interval-selected form;
//! - the causal progress summary a replica's frontier exchanges:
//!   contiguous prefix plus received intervals, as canonical
//!   `CausalContext` bytes;
//! - the stable-frontier acknowledgement: canonical `StableFrontier`
//!   bytes derived from member attestations;
//! - the anti-entropy causal summary: the domain-separated causal
//!   digest of the progress summary and the dual Merkle roots of a
//!   real replica at one frontier; and
//! - the durable replica snapshot form.
//!
//! Every pinned value is architecture-independent: all integers are
//! fixed-width `u64` (big-endian or minimal LEB128), identities are
//! fixed 16-byte replicas, collections iterate in `BTree` order, field
//! values carry exact IEEE-754 bit patterns, and digests are BLAKE3
//! over those bytes. No `usize` width, pointer width, hash-map
//! iteration order, or platform float formatting participates in any
//! pinned value, so one replica set may mix `x86_64` and `aarch64`
//! Members against the same vectors.

#![allow(missing_docs)]

use std::collections::BTreeMap;
use std::fmt::Write;

use terrabase_causal::{
    ActorProgress, CanonicalEncode, CausalContext, CounterInterval, OriginCounter, StableFrontier,
    decode_canonical,
};
use terrabase_replication_core::anti_entropy::{causal_hash, materialized_hash, tree_for_replica};
use terrabase_replication_core::{
    ReplicaDoc, ReplicaExchange, ReplicaFrontier, ReplicaValue, actor_from_peer,
};

/// The exchange codec major version byte every frame opens with.
const EXCHANGE_CODEC_MAJOR: u8 = 1;

/// The pinned replica: member 7 authors four operations — a two-field
/// put, a put, a delete, and a whole-body replace that drops a field —
/// and absorbs one operation authored by member 8. All physical times
/// are fixed, so every dot, HLC stamp, observed context, and register
/// value is fixed.
fn fixture_replica() -> ReplicaDoc {
    let mut replica = ReplicaDoc::new(actor_from_peer(7));
    replica
        .apply_put(1_000, "doc-1", &body(&[("kind", text("user")), ("status", text("active"))]))
        .unwrap();
    replica.apply_put(1_100, "doc-2", &body(&[("kind", text("order"))])).unwrap();
    replica.apply_delete(1_200, "doc-2").unwrap();
    replica.apply_put(1_300, "doc-1", &body(&[("status", text("idle"))])).unwrap();

    let mut peer = ReplicaDoc::new(actor_from_peer(8));
    peer.apply_put(1_050, "doc-3", &body(&[("tier", text("gold"))])).unwrap();
    let ReplicaExchange::Delta(bytes) = peer.delta_for(&ReplicaFrontier::default()).unwrap() else {
        panic!("the peer delta must encode as a batch");
    };
    replica.absorb(&bytes).unwrap();
    replica
}

fn body(fields: &[(&str, ReplicaValue)]) -> BTreeMap<String, ReplicaValue> {
    fields.iter().map(|(key, value)| ((*key).to_owned(), value.clone())).collect()
}

fn text(value: &str) -> ReplicaValue {
    ReplicaValue::Text(value.to_owned())
}

fn delta_bytes(replica: &ReplicaDoc, remote: &ReplicaFrontier) -> Vec<u8> {
    let ReplicaExchange::Delta(bytes) = replica.delta_for(remote).unwrap() else {
        panic!("the delta must encode as a batch");
    };
    bytes
}

/// The pinned causal progress summary: actor 0x11 holds contiguous
/// prefix 3 plus received intervals 5..=7 and 10..=12; actor 0x22 holds
/// contiguous prefix 4 with nothing above it.
fn summary_context() -> CausalContext {
    let alpha = actor_from_peer(0x11);
    let beta = actor_from_peer(0x22);
    let mut alpha_progress = ActorProgress::new();
    alpha_progress.insert_interval(CounterInterval::new(1, 3).unwrap());
    alpha_progress.insert_interval(CounterInterval::new(5, 7).unwrap());
    alpha_progress.insert_interval(CounterInterval::new(10, 12).unwrap());
    let mut beta_progress = ActorProgress::new();
    beta_progress.insert_interval(CounterInterval::new(1, 4).unwrap());
    let mut context = CausalContext::new();
    context.insert_progress(alpha, &alpha_progress);
    context.insert_progress(beta, &beta_progress);
    context
}

/// The same dot set assembled one counter at a time, in shuffled order,
/// with the actors inserted in the opposite order: canonical
/// normalization must erase every trace of how the summary was built.
fn summary_context_shuffled() -> CausalContext {
    let alpha = actor_from_peer(0x11);
    let beta = actor_from_peer(0x22);
    let mut alpha_progress = ActorProgress::new();
    alpha_progress.insert_interval(CounterInterval::new(10, 12).unwrap());
    for counter in [3_u64, 1, 2, 6, 7, 5] {
        alpha_progress.insert(OriginCounter::new(counter).unwrap());
    }
    let mut beta_progress = ActorProgress::new();
    for counter in [4_u64, 2, 1, 3] {
        beta_progress.insert(OriginCounter::new(counter).unwrap());
    }
    let mut context = CausalContext::new();
    context.insert_progress(beta, &beta_progress);
    context.insert_progress(alpha, &alpha_progress);
    context
}

/// The pinned stable-frontier acknowledgement: members 0x11, 0x22, and
/// 0x33 are required. An attestation names only its contiguous prefix —
/// the sparse interval 9..=11 above member 0x11's prefix contributes
/// nothing, and the absent member 0x33 contributes zero (the ADR 0023
/// retention rule).
fn acknowledged_frontier() -> StableFrontier {
    let alpha = actor_from_peer(0x11);
    let beta = actor_from_peer(0x22);
    let gamma = actor_from_peer(0x33);
    let mut alpha_progress = ActorProgress::new();
    alpha_progress.insert_interval(CounterInterval::new(1, 5).unwrap());
    alpha_progress.insert_interval(CounterInterval::new(9, 11).unwrap());
    let mut beta_progress = ActorProgress::new();
    beta_progress.insert_interval(CounterInterval::new(1, 4).unwrap());
    StableFrontier::from_required_attestations(
        [alpha, beta, gamma],
        [(alpha, alpha_progress), (beta, beta_progress)],
    )
}

fn hex(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(output, "{byte:02x}").unwrap();
    }
    output
}

fn fixture(name: &str) -> String {
    std::fs::read_to_string(format!("{}/tests/vectors/adr0023/{name}", env!("CARGO_MANIFEST_DIR")))
        .unwrap()
}

/// The full member-to-member frame: codec major, operation count, then
/// the five operations in canonical dot order.
#[test]
fn golden_delta_batch_frame_bytes_are_stable() {
    let replica = fixture_replica();
    let bytes = delta_bytes(&replica, &ReplicaFrontier::default());
    let canonical = hex(&bytes);
    assert_eq!(bytes[0], EXCHANGE_CODEC_MAJOR);
    assert_eq!(bytes[1], 5, "the frame carries five operations");
    assert_eq!(
        canonical,
        "0105000000000000000000000000000000070101e8070001000105646f632d3102046b696e64010773343a7573657206737461747573010973363a616374697665000000000000000000000000000000070102cc0800010100000000000000000000000000000007000000000000000101000105646f632d3201046b696e64010873353a6f72646572000000000000000000000000000000070103b00900010100000000000000000000000000000007000000000000000102000205646f632d32000000000000000000000000000000070104940a00010100000000000000000000000000000007000000000000000103000105646f632d3102046b696e640006737461747573010773343a69646c650000000000000000000000000000000801019a080001000105646f632d33010474696572010773343a676f6c64"
    );
    let vector = fixture("replication-frame-full-delta-batch.json");
    assert!(
        vector.contains("terrabase-native/1") && vector.contains(&canonical),
        "the fixture must carry the pinned frame bytes"
    );

    // The frame decodes back into an equal replica and re-exports to the
    // same bytes: the pin holds through a full codec round trip.
    let mut restored = ReplicaDoc::new(actor_from_peer(9));
    restored.absorb(&bytes).unwrap();
    assert_eq!(restored.materialized(), replica.materialized());
    assert_eq!(delta_bytes(&restored, &ReplicaFrontier::default()), bytes);
}

/// The interval-selected frame: a remote frontier covering member 7's
/// counters 1..=2 receives exactly the three operations it is missing.
#[test]
fn golden_delta_batch_selects_only_missing_intervals() {
    let replica = fixture_replica();
    let remote = ReplicaFrontier::from_ends(vec![(actor_from_peer(7), 2)]);
    let bytes = delta_bytes(&replica, &remote);
    let canonical = hex(&bytes);
    assert_eq!(bytes[0], EXCHANGE_CODEC_MAJOR);
    assert_eq!(bytes[1], 3, "only the three uncovered operations travel");
    assert_eq!(
        canonical,
        "0103000000000000000000000000000000070103b00900010100000000000000000000000000000007000000000000000102000205646f632d32000000000000000000000000000000070104940a00010100000000000000000000000000000007000000000000000103000105646f632d3102046b696e640006737461747573010773343a69646c650000000000000000000000000000000801019a080001000105646f632d33010474696572010773343a676f6c64"
    );
    let vector = fixture("replication-frame-partial-delta-batch.json");
    assert!(
        vector.contains("terrabase-native/1") && vector.contains(&canonical),
        "the fixture must carry the pinned frame bytes"
    );

    // A replica that already covered counters 1..=2 converges to the same
    // materialized state from the selected frame alone.
    let mut caught_up = ReplicaDoc::new(actor_from_peer(9));
    caught_up.absorb(&bytes).unwrap();
    assert_eq!(caught_up.materialized(), replica.materialized());
}

/// The causal progress summary: codec major, actor count, then per
/// actor in canonical identity order the contiguous prefix, the
/// interval count, and each closed received interval.
#[test]
fn golden_progress_summary_bytes_are_stable() {
    let context = summary_context();
    let canonical = hex(&context.canonical_bytes());
    assert_eq!(
        canonical,
        "0102000000000000000000000000000000110000000000000001030205070a0c0000000000000000000000000000002200000000000000010400"
    );
    assert_eq!(
        hex(&summary_context_shuffled().canonical_bytes()),
        canonical,
        "canonical normalization must erase the assembly order"
    );
    let vector = fixture("progress-summary-causal-context.json");
    assert!(
        vector.contains("terrabase-native/1") && vector.contains(&canonical),
        "the fixture must carry the pinned summary bytes"
    );

    let decoded: CausalContext = decode_canonical(&context.canonical_bytes()).unwrap();
    assert_eq!(decoded, context);
    assert_eq!(hex(&decoded.canonical_bytes()), canonical);
}

/// The stable-frontier acknowledgement: codec major, member count, then
/// per required member in canonical identity order the acknowledged
/// contiguous prefix. Sparse attestations and absent members pin to
/// zero.
#[test]
fn golden_stable_frontier_acknowledgement_bytes_are_stable() {
    let frontier = acknowledged_frontier();
    assert_eq!(frontier.through(actor_from_peer(0x11)), 5);
    assert_eq!(frontier.through(actor_from_peer(0x22)), 4);
    assert_eq!(frontier.through(actor_from_peer(0x33)), 0);
    let canonical = hex(&frontier.canonical_bytes());
    assert_eq!(
        canonical,
        "0103000000000000000000000000000000110000000000000001050000000000000000000000000000002200000000000000010400000000000000000000000000000033000000000000000100"
    );
    let vector = fixture("stable-frontier-acknowledgement.json");
    assert!(
        vector.contains("terrabase-native/1") && vector.contains(&canonical),
        "the fixture must carry the pinned acknowledgement bytes"
    );

    let decoded: StableFrontier = decode_canonical(&frontier.canonical_bytes()).unwrap();
    assert_eq!(hex(&decoded.canonical_bytes()), canonical);
    assert_eq!(decoded.through(actor_from_peer(0x11)), 5);
    assert_eq!(decoded.through(actor_from_peer(0x33)), 0);
}

/// The anti-entropy causal summary: the domain-separated BLAKE3 digest
/// of the pinned progress summary bytes, the materialized digest
/// domain, and the dual Merkle roots of the pinned replica.
#[test]
fn golden_anti_entropy_causal_summary_is_stable() {
    let causal_digest = hex(&causal_hash(&summary_context()));
    assert_eq!(causal_digest, "c79fe03e42e3a05220c92f855dc020171a1ca808f0d4bde27c4b64a779f5b2ba",);
    let materialized_digest = hex(&materialized_hash(b"pin:materialized-domain"));
    assert_eq!(
        materialized_digest,
        "d83cfb1693928ab2b8414757131efe5a9597b406a750eabdb9d4da2fe682b177",
    );

    let replica = fixture_replica();
    let roots = tree_for_replica(&replica, 2, 64).unwrap().roots();
    let causal_root = hex(&roots.causal);
    let materialized_root = hex(&roots.materialized);
    assert_eq!(causal_root, "fd880234da64058a1a39e648779ad3ad2b45063be428aa2d6d1c639fc3c66f49",);
    assert_eq!(
        materialized_root,
        "90e0ec294fdf9b131ebb3e5a6c0eaae85ec4460162aecff1b7ffdcd2f9139977",
    );
    let vector = fixture("anti-entropy-causal-summary.json");
    assert!(
        vector.contains("terrabase-native/1")
            && vector.contains(&causal_digest)
            && vector.contains(&causal_root)
            && vector.contains(&materialized_root)
            && vector.contains(&materialized_digest),
        "the fixture must carry every pinned digest"
    );
}

/// The durable replica snapshot: codec major, actor identity, sequencer
/// position, clock, retained floor, materialized registers, then the
/// same operation encoding the delta frame carries.
#[test]
fn golden_snapshot_bytes_are_stable() {
    let replica = fixture_replica();
    let bytes = replica.snapshot_bytes().unwrap();
    let canonical = hex(&bytes);
    assert_eq!(bytes[0], EXCHANGE_CODEC_MAJOR);
    assert_eq!(
        canonical,
        "01000000000000000000000000000000070105940a0101000305646f632d310002046b696e644301010401000000000000000000000000000000070104000100000000000000000000000000000000070104000100000000000000000000000000000007010300940a00067374617475734c01010401000000000000000000000000000000070104000101000000000000000000000000000000070104000100000000000000000000000000000007010300940a00040773343a69646c6505646f632d320100000000000000000000000000000007010301046b696e644d01010401000000000000000000000000000000070102000101000000000000000000000000000000070102000100000000000000000000000000000007010100cc0800040873353a6f7264657205646f632d3300010474696572390101040100000000000000000000000000000008010100010100000000000000000000000000000008010100009a0800040773343a676f6c6405000000000000000000000000000000070101e8070001000105646f632d3102046b696e64010773343a7573657206737461747573010973363a616374697665000000000000000000000000000000070102cc0800010100000000000000000000000000000007000000000000000101000105646f632d3201046b696e64010873353a6f72646572000000000000000000000000000000070103b00900010100000000000000000000000000000007000000000000000102000205646f632d32000000000000000000000000000000070104940a00010100000000000000000000000000000007000000000000000103000105646f632d3102046b696e640006737461747573010773343a69646c650000000000000000000000000000000801019a080001000105646f632d33010474696572010773343a676f6c64"
    );
    let vector = fixture("replica-snapshot.json");
    assert!(
        vector.contains("terrabase-native/1") && vector.contains(&canonical),
        "the fixture must carry the pinned snapshot bytes"
    );

    // The snapshot restores into a replica with the same frontier and the
    // same materialized state, and its operation tail is byte-identical
    // to the full delta frame's operation section: one operation
    // encoding serves both forms.
    let restored = ReplicaDoc::from_snapshot(actor_from_peer(7), &bytes).unwrap();
    assert_eq!(restored.frontier(), replica.frontier());
    assert_eq!(restored.materialized(), replica.materialized());
    let frame = delta_bytes(&replica, &ReplicaFrontier::default());
    assert_eq!(&bytes[bytes.len() - (frame.len() - 2)..], &frame[2..]);
}
