//! SH-02 contract tests: shard topology, bucket assignment, and routing
//! (PB-5036/5037, ADR 0011).
//!
//! Scope proved here: the fixed bucket space and the hash identity of the
//! bucket derivation (anchored to reference XXH64 digests), bucket-range
//! table validation at the accepted topology-limit boundaries, the canonical
//! shard identity convention, deterministic two-step routing over a
//! caller-supplied `TopologyView`, explicit bucket moves as the only table
//! edit, epoch rules on ownership transitions, and the tenant-isolation
//! probe across shards.
//!
//! Non-claims: no migration streaming (SH-03), no sharded query fan-out
//! (SH-04), no failover lane (SH-05), and no performance fact appears in any
//! test name or assertion.

#![allow(
    clippy::cast_possible_truncation,
    clippy::unreadable_literal,
    reason = "digests reduced modulo 2^20 are below u32::MAX by construction, and the remaining hex literals are verbatim reference anchors, not numbers to read"
)]

use crate::replication::{MemberId, TopologyView};
use crate::sharding::{
    BUCKET_SPACE, Bucket, BucketRange, BucketRangeTable, CollectionShardKey, MAX_SHARDS,
    MIN_INITIAL_BUCKET_ALLOCATION, RoutePlacement, RoutingRequest, ShardId, ShardKey,
    ShardKeyField, ShardKeyKind, ShardKeySet, ShardTopology, ShardingError,
};
use crate::types::{CollectionName, Document, DocumentId, DocumentValue, TenantId};

/// Reference XXH64 digests (seed 0, UTF-8 bytes) produced with the cached
/// reference implementation `xxhash-rust` 0.8.18 — the same algorithm family
/// V1's `XXHash64(lowercased id)` uses. The bucket value itself is not
/// observable parity; these anchors fix the hash identity SH-02 pins.
static REFERENCE_XXH64: &[(&str, u64)] = &[
    ("", 0xef46db3751d8e999),
    ("a", 0xd24ec4f1a98c6e5b),
    ("z", 0x048a5a7677a8e488),
    ("0", 0x633457081244afec),
    ("orders/1", 0xc47b7a7a7ef17a04),
    ("orders/1001", 0xbfe20357a87b090b),
    ("orders/1002", 0x3e009291bf1c56d5),
    ("orders/1003", 0xf9db6e933984031e),
    ("users/9f2c7d1e-44ab-4c0f-9d3a-2b8e1f6a0c55", 0xade728b28bcba7ba),
    ("users/9F2C7D1E-44AB-4C0F-9D3A-2B8E1F6A0C55", 0x0c52be4a62be9312),
    ("caf\u{e9}/\u{fc}n\u{ef}code-1", 0xd1f80a5cd4dd47dc),
    ("identity-anchor-alpha", 0xf1c894a06b65b111),
    ("identity-anchor-beta", 0x4c7b0b5640be09f5),
    ("identity-anchor-gamma", 0x23f2a9197c7cd093),
    // Composite canonical forms: decimal byte length, U+001F, rendered
    // component, repeated per component.
    ("6\u{1f}s:beta", 0xb41e60c182a15808),
    ("5\u{1f}i:-426\u{1f}b:true", 0x42852ebba112af63),
];

/// Long-input reference anchors (repeat is not const-evaluable, so these
/// join the corpus at run time). Lengths cross and exceed the 32-byte XXH64
/// chunk boundary on purpose.
fn long_reference_inputs() -> Vec<(String, u64)> {
    vec![
        ("x".repeat(40), 0x926f564e1b3e18d5),
        ("x".repeat(64), 0xa97161d5402f516e),
        ("x".repeat(100), 0x92f0de5a88a3c094),
        ("x".repeat(255), 0x568b95dc7ddbe218),
    ]
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection")
}

fn shard(number: u32) -> ShardId {
    ShardId::from_number(number)
}

fn bucket(value: u32) -> Bucket {
    Bucket::try_new(value).expect("bucket inside the fixed space")
}

fn range(start: u32, end: u32, number: u32) -> BucketRange {
    BucketRange::try_new(start, end, shard(number)).expect("valid bucket range")
}

fn ranges(values: &[(u32, u32, u32)]) -> Vec<BucketRange> {
    values.iter().map(|(start, end, number)| range(*start, *end, *number)).collect()
}

/// One Region, one Member, one owned shard range: the degenerate unsharded
/// membership view.
fn single_shard_view(number: u32, member: &str, epoch: u64) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member(member, "r-alpha", "r-alpha")
        .expect("valid member")
        .with_owner_at_epoch(&shard(number).as_range(), member, epoch)
        .expect("valid owner")
        .validated()
        .expect("servable view")
}

/// A balanced N-shard table: N equal ranges partitioning [0, 2^20).
fn balanced_table(count: u32) -> BucketRangeTable {
    let width = BUCKET_SPACE / count;
    let entries: Vec<BucketRange> =
        (0..count).map(|index| range(index * width, (index + 1) * width, index)).collect();
    BucketRangeTable::try_new(&entries).expect("balanced table validates")
}

/// Two Regions, two Members, two owned shard ranges at one epoch.
fn two_shard_view(epoch: u64) -> TopologyView {
    two_shard_view_at(epoch, epoch)
}

/// Two shards owned at explicit epochs.
fn two_shard_view_at(epoch_zero: u64, epoch_one: u64) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", epoch_zero)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", epoch_one)
        .expect("valid owner")
        .validated()
        .expect("servable view")
}

/// Two shards, shard/1 handed from `m-beta` to `m-alpha` at `epoch`. The
/// `TopologyView` builder refuses re-assignment of an owned range, so a
/// swapped seating is assembled from parts, exactly as the control plane
/// would issue it.
fn swapped_from_parts(epoch: u64) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", epoch)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-alpha", epoch)
        .expect("valid owner")
        .validated()
        .expect("servable view")
}

/// An eight-shard membership view at `epoch` seated on one Member.
fn eight_shard_view(epoch: u64) -> TopologyView {
    let mut view = TopologyView::new().with_region("r-alpha").expect("valid region");
    view = view.with_member("m-alpha", "r-alpha", "r-alpha").expect("valid member");
    for number in 0..8u32 {
        view = view
            .with_owner_at_epoch(&shard(number).as_range(), "m-alpha", epoch)
            .expect("valid owner");
    }
    view.validated().expect("servable view")
}

fn two_shard_topology() -> ShardTopology {
    ShardTopology::try_new(balanced_table(2), two_shard_view(1)).expect("valid shard topology")
}

/// One owned routing fixture: the request borrows these values, so the
/// fixture owns them.
struct Fixture {
    scope: TenantId,
    collection: CollectionName,
    document: DocumentId,
}

impl Fixture {
    /// Builds one `orders` fixture for a tenant and document identity.
    fn new(scope: &str, name: &str) -> Self {
        Self { scope: tenant(scope), collection: collection("orders"), document: document_id(name) }
    }

    /// Returns the identity-keyed routing request for this fixture.
    fn request(&self) -> RoutingRequest<'_> {
        RoutingRequest::by_identity(&self.scope, &self.collection, &self.document)
    }
}

fn object_value(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    let mut map = std::collections::BTreeMap::new();
    for (name, value) in fields {
        map.insert((*name).to_owned(), value.clone());
    }
    DocumentValue::Object(map)
}

#[test]
fn bucket_space_and_topology_limits_match_the_accepted_record() {
    // ADR 0011 D2 and docs/sharding/topology-limits.md: 2^20 buckets
    // (permanent invariant), at most 128 shards, 8,192-bucket initial
    // allocation floor.
    assert_eq!(BUCKET_SPACE, 1 << 20);
    assert_eq!(BUCKET_SPACE, 1_048_576);
    assert_eq!(MAX_SHARDS, 128);
    assert_eq!(MIN_INITIAL_BUCKET_ALLOCATION, 8_192);
}

#[test]
fn bucket_derivation_matches_the_pinned_reference_hash_identity() {
    for (input, digest) in REFERENCE_XXH64 {
        assert_eq!(crate::sharding::hash::xxh64(input.as_bytes()), *digest, "input {input:?}");
    }
    for (input, digest) in long_reference_inputs() {
        assert_eq!(crate::sharding::hash::xxh64(input.as_bytes()), digest, "input {input:?}");
    }
    // The identity-keyed derivation hashes the lowercased identity with the
    // same fixed function and reduces into [0, 2^20). Identifier-shaped
    // anchors only (the empty string and composite forms are not identities).
    for (input, digest) in REFERENCE_XXH64 {
        if !input.is_empty()
            && input
                .chars()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '/' || c == '-')
        {
            let key = ShardKey::identity(&document_id(input));
            let expected = Bucket::try_new((*digest % u64::from(BUCKET_SPACE)) as u32)
                .expect("digest reduces into the space");
            assert_eq!(key.bucket(), expected, "identity {input:?}");
        }
    }
    // Upper-case identities fold to the lower-case bucket: the V1
    // derivation hashes the lowercased identity.
    let folded = ShardKey::identity(&document_id("users/9F2C7D1E-44AB-4C0F-9D3A-2B8E1F6A0C55"));
    let lower = ShardKey::identity(&document_id("users/9f2c7d1e-44ab-4c0f-9d3a-2b8e1f6a0c55"));
    assert_eq!(folded.bucket(), lower.bucket());
    // Unicode case folding is the Unicode-aware lowercase mapping.
    let folded_unicode = ShardKey::identity(&document_id("CAF\u{c9}/\u{dc}N\u{cf}CODE-1"));
    let lower_unicode = ShardKey::identity(&document_id("caf\u{e9}/\u{fc}n\u{ef}code-1"));
    assert_eq!(folded_unicode.bucket(), lower_unicode.bucket());
    assert_ne!(folded_unicode.bucket(), lower.bucket());
}

#[test]
fn buckets_stay_inside_the_fixed_space() {
    assert!(Bucket::try_new(BUCKET_SPACE - 1).is_ok());
    assert_eq!(
        Bucket::try_new(BUCKET_SPACE),
        Err(ShardingError::BucketAboveSpace { value: BUCKET_SPACE })
    );
    assert_eq!(Bucket::try_new(u32::MAX), Err(ShardingError::BucketAboveSpace { value: u32::MAX }));
    // An empty range can hold no buckets, and a range may not leave the
    // space — the structural refusal of V1's virtual buckets above 2^20
    // (prefixed routing; see the module decision record).
    assert_eq!(
        BucketRange::try_new(5, 5, shard(0)),
        Err(ShardingError::EmptyBucketRange { start: 5, end: 5 })
    );
    assert_eq!(BucketRange::try_new(4, 6, shard(0)).expect("valid range").end(), 6);
    assert_eq!(
        BucketRange::try_new(BUCKET_SPACE - 4, BUCKET_SPACE + 4, shard(0)),
        Err(ShardingError::BucketAboveSpace { value: BUCKET_SPACE + 4 })
    );
}

#[test]
fn bucket_is_a_pure_function_of_the_shard_key() {
    // Invariant 1 of docs/sharding/model.md: the bucket never depends on
    // topology state.
    let identity = document_id("orders/1001");
    let key = ShardKey::identity(&identity);
    let fixture = Fixture::new("tenant-a", "orders/1001");
    let before = two_shard_topology();
    let after =
        ShardTopology::try_new(balanced_table(8), eight_shard_view(3)).expect("valid topology");
    assert_eq!(key.bucket(), ShardKey::identity(&identity).bucket());
    assert_eq!(before.route(&fixture.request()).expect("routes").bucket(), key.bucket());
    assert_eq!(after.route(&fixture.request()).expect("routes").bucket(), key.bucket());
    // Distinct identities reach many buckets of the fixed space (a
    // routing-space observation, not a performance claim).
    let mut distinct = std::collections::BTreeSet::new();
    for index in 0..1000u32 {
        let identity = document_id(&format!("orders/{index}"));
        distinct.insert(ShardKey::identity(&identity).bucket());
    }
    assert!(distinct.len() > 100, "distinct identities reach many buckets");
    for value in distinct {
        assert!(value.get() < BUCKET_SPACE);
    }
}

#[test]
fn composite_shard_keys_are_typed_length_delimited_and_order_sensitive() {
    let beta = ShardKey::composite(&["s:beta".to_owned()]);
    let number = ShardKey::composite(&["i:-42".to_owned(), "b:true".to_owned()]);
    // Anchored to the reference digests of the canonical composite form.
    assert_eq!(beta.bucket(), bucket((0xb41e60c182a15808 % u64::from(BUCKET_SPACE)) as u32));
    assert_eq!(number.bucket(), bucket((0x42852ebba112af63 % u64::from(BUCKET_SPACE)) as u32));
    // Length delimiting: ("ab", "c") and ("a", "bc") are different keys.
    let ab_c = ShardKey::composite(&["ab".to_owned(), "c".to_owned()]);
    let a_bc = ShardKey::composite(&["a".to_owned(), "bc".to_owned()]);
    assert_ne!(ab_c.bucket(), a_bc.bucket());
    // Component order is part of the key.
    let reversed = ShardKey::composite(&["c".to_owned(), "ab".to_owned()]);
    assert_ne!(ab_c.bucket(), reversed.bucket());
    assert_eq!(ab_c.bucket(), ShardKey::composite(&["ab".to_owned(), "c".to_owned()]).bucket());
}

#[test]
fn shard_identities_are_canonical_range_texts() {
    // ADR 0011 D1: the range identifier carried in the view is the shard's
    // stable identity, canonical form `shard/<shard-id>`.
    assert_eq!(shard(7).as_range(), "shard/7");
    assert_eq!(ShardId::from_range_text("shard/7"), Some(shard(7)));
    assert_eq!(ShardId::from_range_text("shard/0"), Some(shard(0)));
    // Exactly one text per identity: leading zeros, other prefixes, empty
    // numbers, and negative or overflowing numbers are not canonical.
    for text in ["shard/07", "Shard/7", "shard/", "shard/-1", "shard/4294967296", "main", ""] {
        assert_eq!(ShardId::from_range_text(text), None, "text {text:?}");
    }
    // The identity survives a bucket move unchanged: resharding changes
    // which buckets a shard owns, never the shard's identity.
    assert_eq!(shard(0).as_range(), "shard/0");
    let moved =
        balanced_table(2).with_bucket_moved(bucket(17), shard(0), shard(1)).expect("explicit move");
    assert_eq!(moved.shard_of(bucket(17)), shard(1));
}

#[test]
fn single_shard_table_is_the_degenerate_unsharded_case() {
    let table = BucketRangeTable::single_shard(shard(0));
    assert_eq!(table.entries(), ranges(&[(0, BUCKET_SPACE, 0)]).as_slice());
    let topology =
        ShardTopology::try_new(table, single_shard_view(0, "m-alpha", 1)).expect("degenerate case");
    for name in ["orders/1", "orders/1002", "users/x"] {
        let fixture = Fixture::new("tenant-a", name);
        let placement = topology.route(&fixture.request()).expect("routes");
        assert_eq!(placement.shard(), shard(0));
        assert_eq!(placement.owner(), &MemberId::try_new("m-alpha").expect("valid member"));
        assert_eq!(placement.epoch(), 1);
    }
}

#[test]
fn table_must_partition_the_bucket_space_exactly() {
    // First entry must start at zero.
    assert_eq!(
        BucketRangeTable::try_new(&ranges(&[(1, BUCKET_SPACE, 0)])),
        Err(ShardingError::TableDoesNotStartAtZero { first_start: 1 })
    );
    // Last entry must end at the space end.
    assert_eq!(
        BucketRangeTable::try_new(&ranges(&[(0, BUCKET_SPACE - 1, 0)])),
        Err(ShardingError::TableDoesNotCoverBucketSpace { last_end: BUCKET_SPACE - 1 })
    );
    // A gap refuses, naming the first bucket no entry covers.
    assert_eq!(
        BucketRangeTable::try_new(&ranges(&[(0, 10, 0), (11, BUCKET_SPACE, 1)])),
        Err(ShardingError::TableGap { at: 10 })
    );
    // An overlap refuses.
    assert_eq!(
        BucketRangeTable::try_new(&ranges(&[(0, 10, 0), (9, BUCKET_SPACE, 1)])),
        Err(ShardingError::TableOverlap { at: 9 })
    );
    // Empty tables refuse.
    assert_eq!(BucketRangeTable::try_new(&[]), Err(ShardingError::EmptyTable));
    // The balanced two-shard table is the reference-valid shape.
    assert_eq!(
        balanced_table(2).entries(),
        ranges(&[(0, BUCKET_SPACE / 2, 0), (BUCKET_SPACE / 2, BUCKET_SPACE, 1)]).as_slice()
    );
}

#[test]
fn adjacent_entries_of_one_shard_are_refused_unless_merged() {
    // V1's merged-continuous rule: adjacent entries never share a shard.
    assert_eq!(
        BucketRangeTable::try_new(&ranges(&[(0, 10, 0), (10, BUCKET_SPACE, 0)])),
        Err(ShardingError::AdjacentShardsNotMerged { at: 10 })
    );
    // The same coverage in one entry is the merged, accepted form.
    assert!(BucketRangeTable::try_new(&ranges(&[(0, BUCKET_SPACE, 0)])).is_ok());
}

#[test]
fn at_most_128_shards_are_accepted() {
    // The boundary pair of docs/sharding/topology-limits.md: 128 accepted,
    // 129 refused.
    let one_hundred_twenty_eight = balanced_table(128);
    assert_eq!(one_hundred_twenty_eight.shards().len(), MAX_SHARDS);
    one_hundred_twenty_eight
        .initial_allocation_guard()
        .expect("balanced initial allocation respects the floor");

    let width = 8_128u32;
    let mut entries =
        ranges(&(0..128u32).map(|i| (i * width, (i + 1) * width, i)).collect::<Vec<_>>());
    entries.push(range(128 * width, BUCKET_SPACE, 128));
    assert_eq!(
        BucketRangeTable::try_new(&entries),
        Err(ShardingError::TooManyShards { count: 129 })
    );
}

#[test]
fn the_allocation_floor_is_a_guard_not_a_runtime_invariant() {
    // A mid-migration shape: shard/0 holds 100 buckets, shard/1 the rest.
    // The table stays valid (migration may transiently fragment below the
    // floor); only the initial-allocation guard refuses it.
    let table = BucketRangeTable::try_new(&ranges(&[(0, 100, 0), (100, BUCKET_SPACE, 1)]))
        .expect("mid-migration shape validates");
    assert_eq!(
        table.initial_allocation_guard(),
        Err(ShardingError::InitialAllocationBelowFloor {
            shard: shard(0),
            buckets: 100,
            floor: MIN_INITIAL_BUCKET_ALLOCATION,
        })
    );
    balanced_table(2).initial_allocation_guard().expect("balanced allocation respects the floor");
}

#[test]
fn bucket_moves_are_explicit_operations_that_preserve_the_partition() {
    let table = balanced_table(2);
    let boundary = BUCKET_SPACE / 2;

    // Moving a mid-range bucket splits the source range and gives the
    // destination a single-bucket range.
    let moved = table.with_bucket_moved(bucket(3), shard(0), shard(1)).expect("explicit move");
    assert_eq!(
        moved.entries(),
        ranges(&[(0, 3, 0), (3, 4, 1), (4, boundary, 0), (boundary, BUCKET_SPACE, 1)]).as_slice()
    );
    assert_eq!(moved.shard_of(bucket(3)), shard(1));
    assert_eq!(moved.bucket_count_of(shard(0)), u64::from(boundary) - 1);

    // Moving the bucket back re-merges to the original table.
    let restored =
        moved.with_bucket_moved(bucket(3), shard(1), shard(0)).expect("explicit move back");
    assert_eq!(restored.entries(), table.entries());

    // Moving to the owning shard is an idempotent no-op.
    let no_op = table.with_bucket_moved(bucket(3), shard(0), shard(0)).expect("no-op move");
    assert_eq!(no_op.entries(), table.entries());

    // A move naming a source that does not own the bucket refuses: the
    // operation is explicit about what moves.
    assert_eq!(
        moved.with_bucket_moved(bucket(3), shard(0), shard(1)),
        Err(ShardingError::BucketNotOwnedBySource {
            bucket: bucket(3),
            source: shard(0),
            owner: shard(1),
        })
    );

    // A move may introduce a shard not yet in the table, within the
    // shard-count limit.
    let grown =
        moved.with_bucket_moved(bucket(5), shard(0), shard(2)).expect("move to a new shard");
    assert_eq!(grown.shards(), vec![shard(0), shard(1), shard(2)]);
    assert_eq!(grown.ranges_of(shard(2)), ranges(&[(5, 6, 2)]).as_slice());
    // Boundary moves (first and last bucket of a range) split correctly.
    let edge = table.with_bucket_moved(bucket(0), shard(0), shard(1)).expect("edge move");
    assert_eq!(
        edge.entries(),
        ranges(&[(0, 1, 1), (1, boundary, 0), (boundary, BUCKET_SPACE, 1)]).as_slice()
    );
}

#[test]
fn per_collection_shard_keys_are_typed_and_default_to_identity() {
    let field = ShardKeyField::try_new("region").expect("valid field name");
    assert_eq!(field.as_str(), "region");
    assert!(ShardKeyField::try_new("").is_err());
    assert!(ShardKeyField::try_new(" leading").is_err());

    let identity = ShardKeyKind::identity();
    let fields = ShardKeyKind::try_fields(&["region", "tier"]).expect("valid fields");
    assert!(identity.is_identity());
    assert!(!fields.is_identity());
    assert_eq!(fields.fields().len(), 2);
    // Duplicate field names refuse: a key names each attribute once.
    assert!(ShardKeyKind::try_fields(&["region", "region"]).is_err());

    let orders = CollectionShardKey::try_new("orders", fields.clone()).expect("valid declaration");
    let duplicate =
        CollectionShardKey::try_new("orders", ShardKeyKind::identity()).expect("valid declaration");
    assert_eq!(
        ShardKeySet::try_new(&[orders.clone(), duplicate]),
        Err(ShardingError::DuplicateCollectionShardKey { collection: collection("orders") })
    );
    // Undeclared collections route identity-keyed; declared ones route on
    // their fields.
    let keys = ShardKeySet::try_new(&[orders]).expect("valid set");
    assert!(keys.declaration_for(&collection("users")).is_identity());
    assert!(!keys.declaration_for(&collection("orders")).is_identity());
    assert_eq!(
        ShardKeySet::identity().declaration_for(&collection("orders")),
        &ShardKeyKind::identity()
    );
}

#[test]
fn field_shard_keys_resolve_typed_field_values_and_fail_closed() {
    let orders = CollectionShardKey::try_new(
        "orders",
        ShardKeyKind::try_fields(&["region", "tier"]).expect("valid fields"),
    )
    .expect("valid declaration");
    let keys = ShardKeySet::try_new(&[orders]).expect("valid set");

    let key = keys
        .resolve(
            &collection("orders"),
            &document_id("orders/1"),
            Some(&object_value(&[
                ("region", DocumentValue::String("eu-1".to_owned())),
                ("tier", DocumentValue::Integer(7)),
            ])),
        )
        .expect("resolves typed fields");
    // The rendered components carry their type tag, so String("5") and
    // Integer(5) never collide.
    let expected = ShardKey::composite(&["s:eu-1".to_owned(), "i:7".to_owned()]);
    assert_eq!(key.as_bytes(), expected.as_bytes());

    // A missing field fails closed, never silently re-keys.
    assert_eq!(
        keys.resolve(
            &collection("orders"),
            &document_id("orders/1"),
            Some(&object_value(&[("region", DocumentValue::String("eu-1".to_owned()))]))
        ),
        Err(ShardingError::ShardKeyFieldMissing {
            collection: collection("orders"),
            field: "tier".to_owned(),
        })
    );
    // Unsupported field values (arrays) refuse.
    assert_eq!(
        keys.resolve(
            &collection("orders"),
            &document_id("orders/1"),
            Some(&object_value(&[
                ("region", DocumentValue::Array(vec![])),
                ("tier", DocumentValue::Integer(7)),
            ]))
        ),
        Err(ShardingError::ShardKeyFieldUnsupported {
            collection: collection("orders"),
            field: "region".to_owned(),
        })
    );
    // Field-keyed collections cannot route by identity alone.
    assert_eq!(
        keys.resolve(&collection("orders"), &document_id("orders/1"), None),
        Err(ShardingError::ShardKeyValueRequired { collection: collection("orders") })
    );
    // Identity-keyed collections never need the value.
    let identity = keys.resolve(&collection("users"), &document_id("users/9"), None);
    assert_eq!(
        identity.expect("identity routes").as_bytes(),
        ShardKey::identity(&document_id("users/9")).as_bytes()
    );
}

#[test]
fn routing_is_deterministic_and_two_step() {
    let topology = two_shard_topology();
    // `orders/1001` buckets above the half boundary: its placement names
    // shard/1, m-beta, epoch 1 (fixture fact, not a routing rule).
    let scope = tenant("tenant-a");
    let fixture = Fixture::new("tenant-a", "orders/1001");
    let request = fixture.request();
    let placement: RoutePlacement = topology.route(&request).expect("routes");
    assert_eq!(placement.tenant(), &scope);
    assert_eq!(placement.bucket(), ShardKey::identity(&document_id("orders/1001")).bucket());
    assert!(placement.bucket().get() >= BUCKET_SPACE / 2);
    assert_eq!(placement.shard(), shard(1));
    assert_eq!(topology.shard_of(placement.bucket()), shard(1));
    assert_eq!(placement.owner(), &MemberId::try_new("m-beta").expect("valid member"));
    assert_eq!(placement.epoch(), 1);
    // Determinism: identical inputs produce identical placements.
    for _ in 0..3 {
        assert_eq!(topology.route(&request).expect("routes"), placement);
    }
    // The document overload carries the value for field-keyed routing.
    let document = Document::new(
        document_id("orders/1001"),
        collection("orders"),
        DocumentValue::String("payload".to_owned()),
    );
    let by_document = RoutingRequest::of_document(&scope, &document);
    assert_eq!(by_document.value(), Some(&DocumentValue::String("payload".to_owned())));
    assert_eq!(topology.route(&by_document).expect("routes"), placement);

    // Field-keyed routing through the same seam.
    let orders = CollectionShardKey::try_new(
        "orders",
        ShardKeyKind::try_fields(&["region"]).expect("valid fields"),
    )
    .expect("valid declaration");
    let keyed = topology.with_shard_keys(ShardKeySet::try_new(&[orders]).expect("valid set"));
    let field_value = object_value(&[("region", DocumentValue::String("eu-1".to_owned()))]);
    let field_request =
        RoutingRequest::by_identity(&fixture.scope, &fixture.collection, &fixture.document)
            .with_value(&field_value);
    let field_placement = keyed.route(&field_request).expect("field-keyed route");
    assert_eq!(field_placement.bucket(), ShardKey::composite(&["s:eu-1".to_owned()]).bucket());
    // A field-keyed route without the value fails closed.
    assert_eq!(
        keyed.route(&request),
        Err(ShardingError::ShardKeyValueRequired { collection: collection("orders") })
    );
}

#[test]
fn shard_topology_seating_requires_one_ownership_per_shard() {
    // The view is unservable.
    assert_eq!(
        ShardTopology::try_new(balanced_table(2), TopologyView::new()),
        Err(ShardingError::ViewNotServable)
    );
    // A table shard with no owner in the view.
    let one_owner = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_owner(&shard(0).as_range(), "m-alpha")
        .expect("valid owner")
        .validated()
        .expect("servable view");
    assert_eq!(
        ShardTopology::try_new(balanced_table(2), one_owner),
        Err(ShardingError::ShardWithoutOwner { shard: shard(1) })
    );
    // A view range outside the canonical shard identity form.
    let plain_range = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_owner("main", "m-alpha")
        .expect("valid owner")
        .with_owner(&shard(1).as_range(), "m-alpha")
        .expect("valid owner")
        .validated()
        .expect("servable view");
    assert_eq!(
        ShardTopology::try_new(balanced_table(2), plain_range),
        Err(ShardingError::ShardIdentityOutsideCanonicalForm { range: "main".to_owned() })
    );
    // A view owning a shard the table gives no buckets.
    let mut grown = two_shard_view(1);
    grown = grown.with_owner_at_epoch(&shard(2).as_range(), "m-alpha", 1).expect("valid owner");
    assert_eq!(
        ShardTopology::try_new(balanced_table(2), grown),
        Err(ShardingError::OwnedRangeWithoutBuckets { shard: shard(2) })
    );
}

#[test]
fn view_transitions_refuse_epoch_regressions() {
    // The topology holds shard/1 at epoch 2.
    let topology =
        ShardTopology::try_new(balanced_table(2), two_shard_view_at(1, 2)).expect("valid topology");
    // An identical refresh at the same epochs is admitted.
    assert_eq!(
        topology.transition(balanced_table(2), two_shard_view_at(1, 2)),
        Ok(topology.clone())
    );
    // A refresh that lowers shard/1's epoch refuses: views that lower an
    // epoch are refused, so stale topology never resurrects.
    assert_eq!(
        topology.transition(balanced_table(2), two_shard_view_at(2, 1)),
        Err(ShardingError::EpochRegression { shard: shard(1), held: 2, offered: 1 })
    );
}

#[test]
fn bucket_ownership_transfers_require_raised_epochs() {
    let topology = two_shard_topology();
    let fixture = Fixture::new("tenant-a", "orders/1001");
    let request = fixture.request();
    let bucket = topology.route(&request).expect("routes").bucket();
    let source = topology.shard_of(bucket);
    let destination = if source == shard(0) { shard(1) } else { shard(0) };
    let moved_table =
        topology.table().with_bucket_moved(bucket, source, destination).expect("explicit move");

    // Same epochs: the transfer refuses — every affected shard must raise
    // its epoch so a stale owner is fenced. The refusal names the first
    // affected shard in canonical identity order.
    let first_affected = source.min(destination);
    assert_eq!(
        topology.transition(moved_table.clone(), two_shard_view(1)),
        Err(ShardingError::EpochNotRaised { shard: first_affected, held: 1, offered: 1 })
    );
    // Raised epochs admit the transfer.
    let transferred =
        topology.transition(moved_table, two_shard_view_at(2, 2)).expect("raised epochs transfer");
    assert_eq!(transferred.shard_of(bucket), destination);
    // A pre-transfer placement for the moved bucket no longer routes there.
    let stale = topology.route(&request).expect("routes");
    assert_eq!(
        transferred.authorize_placement(&stale, &tenant("tenant-a")),
        Err(ShardingError::PlacementStale { bucket, was_shard: source, now_shard: destination })
    );
}

#[test]
fn leader_transitions_fence_stale_placements() {
    let topology = two_shard_topology();
    // `orders/1002` buckets above the half boundary: it seats on shard/1.
    let fixture = Fixture::new("tenant-a", "orders/1002");
    let request = fixture.request();
    let placement = topology.route(&request).expect("routes before");
    assert_eq!(placement.shard(), shard(1));

    // A leader hand-over without raising the epoch refuses.
    assert_eq!(
        topology.transition(balanced_table(2), swapped_from_parts(1)),
        Err(ShardingError::EpochNotRaised { shard: shard(1), held: 1, offered: 1 })
    );
    // With the epoch raised the new seating is admitted and the placement
    // names the new owner at the new epoch.
    let seated =
        topology.transition(balanced_table(2), swapped_from_parts(2)).expect("raised epoch");
    let after = seated.route(&request).expect("routes after");
    assert_eq!(after.owner(), &MemberId::try_new("m-alpha").expect("valid member"));
    assert_eq!(after.epoch(), 2);
    assert_eq!(
        seated.authorize_placement(&placement, &tenant("tenant-a")),
        Err(ShardingError::PlacementOwnerSuperseded {
            shard: shard(1),
            was: MemberId::try_new("m-beta").expect("valid member"),
            now: MemberId::try_new("m-alpha").expect("valid member"),
        })
    );
    // An epoch raise with the same owner still fences the old placement.
    let raised = topology.transition(balanced_table(2), two_shard_view(5)).expect("epoch refresh");
    assert_eq!(
        raised.authorize_placement(&placement, &tenant("tenant-a")),
        Err(ShardingError::PlacementEpochSuperseded { shard: shard(1), was: 1, now: 5 })
    );
}

#[test]
fn tenant_isolation_probe_holds_across_shards() {
    let topology = two_shard_topology();
    // Two tenants, identical document identities: both route (a bucket is
    // tenant-agnostic), and each placement is bound to its tenant.
    let tenant_a = tenant("tenant-a");
    let tenant_b = tenant("tenant-b");
    let fixture_a = Fixture::new("tenant-a", "orders/1");
    let fixture_b = Fixture::new("tenant-b", "orders/1");
    let placement_a = topology.route(&fixture_a.request()).expect("tenant-a routes");
    let placement_b = topology.route(&fixture_b.request()).expect("tenant-b routes");
    assert_eq!(placement_a.bucket(), placement_b.bucket());
    assert_eq!(placement_a.shard(), placement_b.shard());
    assert_ne!(placement_a.tenant(), placement_b.tenant());
    assert_eq!(placement_a.tenant(), &tenant_a);

    // The probe: a hostile requester presents tenant-a's placement while
    // naming tenant-b. The seam denies it with a typed error, in both
    // directions, and never honors a placement under a foreign tenant.
    assert_eq!(
        topology.authorize_placement(&placement_a, &tenant_b),
        Err(ShardingError::TenantMismatch {
            routed_for: tenant("tenant-a"),
            requested_by: tenant("tenant-b"),
        })
    );
    assert_eq!(
        topology.authorize_placement(&placement_b, &tenant_a),
        Err(ShardingError::TenantMismatch {
            routed_for: tenant("tenant-b"),
            requested_by: tenant("tenant-a"),
        })
    );
    assert_eq!(topology.authorize_placement(&placement_a, &tenant_a), Ok(()));
    // The denial survives a topology transition: isolation is enforced per
    // placement, not per routing cache.
    let moved = topology.transition(balanced_table(2), two_shard_view(2)).expect("epoch refresh");
    assert!(matches!(
        moved.authorize_placement(&placement_a, &tenant_b),
        Err(ShardingError::TenantMismatch { .. })
    ));
}
