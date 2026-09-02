//! The ADR 0027 identity corpus.
//!
//! One corpus covers the rows the ADR names as the cross-language minimum:
//! the ULID specification vectors, minimum and maximum values,
//! same-millisecond monotonic generation, entropy overflow, invalid and
//! ambiguous text, wrong-length binary, text/binary round trips, byte
//! ordering, storage-key ordering, hash-to-bucket placement, concurrent
//! generation, and the Region boundary row (a Region has no ULID; only
//! registered uppercase airport `RegionCode` values are accepted). The rows
//! here are engine-side evidence, not a qualification claim: protobuf
//! representation and Rust-to-Go agreement land with the private TBEP
//! conformance corpus under ADR 0025.

// TerraBase and ULID are proper names, not items to link.
#![allow(clippy::doc_markdown)]

use std::collections::HashSet;
use std::sync::Arc;
use std::sync::atomic::AtomicU64;
use std::sync::atomic::Ordering;

use super::Clock;
use super::IdentityPlacementError;
use super::MAX_ENTROPY;
use super::MAX_TIMESTAMP_MILLIS;
use super::MonotonicGenerator;
use super::ULID_BINARY_BYTES;
use super::ULID_TEXT_LENGTH;
use super::Ulid;
use super::UlidError;
use super::compare_storage_keys;
use super::placement_bucket;
use super::placement_digest;
use crate::identity::generate;
use crate::regions::RegionCatalog;
use crate::regions::RegionCodeError;

/// `01ARZ3NDEKTSV4RRFFQ69G5FAV` — the canonical example of the ULID
/// specification.
const SPEC_EXAMPLE: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

/// The specification's all-zero vector, in text.
const SPEC_MINIMUM: &str = "00000000000000000000000000";

/// The specification's maximal vector, in text.
const SPEC_MAXIMUM: &str = "7ZZZZZZZZZZZZZZZZZZZZZZZZZ";

/// The exact 16-byte binary form of [`SPEC_EXAMPLE`], decoded independently
/// of this implementation before being recorded here.
const SPEC_EXAMPLE_BYTES: [u8; ULID_BINARY_BYTES] =
    [1, 86, 62, 58, 181, 211, 214, 118, 76, 97, 239, 185, 147, 2, 189, 91];

/// The 48-bit timestamp component of [`SPEC_EXAMPLE`], in Unix milliseconds.
const SPEC_EXAMPLE_MILLIS: u64 = 1_469_922_850_259;

/// The 80-bit entropy component of [`SPEC_EXAMPLE`].
const SPEC_EXAMPLE_ENTROPY: u128 = 1_012_768_647_078_601_740_696_923;

/// A fixed millisecond used by the deterministic generator rows.
const FIXED_MILLIS: u64 = 1_700_000_000_000;

/// The thread and per-thread value counts of the concurrent generation row.
const CONCURRENT_THREADS: usize = 8;
const CONCURRENT_VALUES_PER_THREAD: usize = 1024;

/// The typed names of the whole ADR 0027 entity list, used by the corpus to
/// prove the list is exactly the ADR's and contains no Region identity.
const ENTITY_IDENTITY_NAMES: &[&str] = &[
    "terrabase::identity::ClusterUlid",
    "terrabase::identity::TenantUlid",
    "terrabase::identity::ProjectUlid",
    "terrabase::identity::DatabaseUlid",
    "terrabase::identity::PrincipalUlid",
    "terrabase::identity::AuthorizationPolicyUlid",
    "terrabase::identity::PrincipalSessionUlid",
    "terrabase::identity::PrincipalGrantUlid",
    "terrabase::identity::CollectionInstanceUlid",
    "terrabase::identity::DocumentUlid",
    "terrabase::identity::DocumentIncarnationUlid",
    "terrabase::identity::RelationshipEdgeUlid",
    "terrabase::identity::MemberUlid",
    "terrabase::identity::ReplicaUlid",
    "terrabase::identity::PartitionUlid",
    "terrabase::identity::SchemaCatalogEntryUlid",
    "terrabase::identity::AutoIndexUlid",
    "terrabase::identity::ViewPlanUlid",
    "terrabase::identity::SubscriptionUlid",
    "terrabase::identity::CommitUlid",
    "terrabase::identity::SnapshotUlid",
    "terrabase::identity::BackupUlid",
    "terrabase::identity::RestoreUlid",
    "terrabase::identity::LongRunningOperationUlid",
    "terrabase::identity::ClientRequestUlid",
    "terrabase::identity::CorrelationUlid",
    "terrabase::identity::CausationUlid",
];

/// Returns a clock that always reports one fixed millisecond, for the
/// same-millisecond rows.
fn frozen_clock() -> Clock {
    Arc::new(|| FIXED_MILLIS)
}

#[test]
fn specification_vectors_parse_to_the_exact_components_and_binary_form() {
    let minimum = Ulid::from_text(SPEC_MINIMUM).expect("the all-zero vector parses");
    assert_eq!(minimum.to_bytes(), [0; ULID_BINARY_BYTES]);
    assert_eq!(minimum.timestamp_millis(), 0);
    assert_eq!(minimum.entropy(), 0);
    assert_eq!(minimum, Ulid::min());

    let maximum = Ulid::from_text(SPEC_MAXIMUM).expect("the maximal vector parses");
    assert_eq!(maximum.to_bytes(), [0xFF; ULID_BINARY_BYTES]);
    assert_eq!(maximum.timestamp_millis(), MAX_TIMESTAMP_MILLIS);
    assert_eq!(maximum.entropy(), MAX_ENTROPY);
    assert_eq!(maximum, Ulid::max());
    assert!(minimum < maximum);

    let example = Ulid::from_text(SPEC_EXAMPLE).expect("the canonical example parses");
    assert_eq!(example.to_bytes(), SPEC_EXAMPLE_BYTES);
    assert_eq!(example.timestamp_millis(), SPEC_EXAMPLE_MILLIS);
    assert_eq!(example.entropy(), SPEC_EXAMPLE_ENTROPY);
    assert_eq!(example.to_text(), SPEC_EXAMPLE);
}

#[test]
fn the_pinned_ulid_crate_agrees_on_the_canonical_vectors() {
    // The wrapper and the pinned dependency must agree on every canonical
    // value: the wrapper is strict *additional* refusal, never a different
    // encoding of the accepted set.
    for text in [SPEC_MINIMUM, SPEC_MAXIMUM, SPEC_EXAMPLE] {
        let ours = Ulid::from_text(text).expect("canonical text parses");
        let theirs = ::ulid::Ulid::from_string(text).expect("the pinned crate agrees");
        assert_eq!(ours.to_bytes(), theirs.to_bytes());
        let mut buffer = [0; ULID_TEXT_LENGTH];
        assert_eq!(theirs.array_to_str(&mut buffer), text);
    }
}

#[test]
fn minimum_and_maximum_round_trip_through_both_forms() {
    for value in [Ulid::min(), Ulid::max()] {
        let text = value.to_text();
        assert_eq!(text.len(), ULID_TEXT_LENGTH);
        assert_eq!(Ulid::from_text(&text), Ok(value));
        assert_eq!(Ulid::from_binary(&value.to_bytes()), Ok(value));
    }
}

#[test]
fn invalid_and_ambiguous_text_is_refused_with_the_typed_class() {
    // Wrong lengths: empty, short, long, padded, and the specification's
    // braced display form (braces are presentation, not identity).
    for (text, expected) in [
        ("", UlidError::WrongTextLength(0)),
        ("01ARZ3NDEKTSV4RRFFQ69G5FA", UlidError::WrongTextLength(25)),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAVX", UlidError::WrongTextLength(27)),
        (" 01ARZ3NDEKTSV4RRFFQ69G5FAV", UlidError::WrongTextLength(27)),
        ("{01ARZ3NDEKTSV4RRFFQ69G5FAV}", UlidError::WrongTextLength(28)),
    ] {
        assert_eq!(Ulid::from_text(text), Err(expected), "refused {text:?} by length");
    }

    // Ambiguous Crockford characters, in both cases: the specification
    // excludes I, L, O, and U so a transliterated 1/I or 0/O confusion can
    // never silently select another identity.
    for (text, index) in [
        ("01ARZ3NDEKTSV4RRFFQ69G5FAI", 25),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAL", 25),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAO", 25),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAU", 25),
        ("0lARZ3NDEKTSV4RRFFQ69G5FAV", 1),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAo", 25),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAu", 25),
    ] {
        let byte = text.as_bytes()[index];
        assert_eq!(
            Ulid::from_text(text),
            Err(UlidError::AmbiguousCharacter { index, byte }),
            "refused the ambiguous character in {text:?}",
        );
    }

    // Lowercase text of an otherwise canonical value: never normalized.
    assert_eq!(
        Ulid::from_text("01arz3ndektsv4rrffq69g5fav"),
        Err(UlidError::NonCanonicalCharacter { index: 2, byte: b'a' }),
    );
    // Non-alphabet bytes anywhere else: hyphens, braces, whitespace.
    for (text, index) in [
        ("01ARZ3NDEKTSV4RRFFQ69G5F-V", 24),
        ("01ARZ3NDEKTSV4RRFFQ69G5F V", 24),
        ("01ARZ3NDEKTSV4RRFFQ69G5F_V", 24),
    ] {
        let byte = text.as_bytes()[index];
        assert_eq!(
            Ulid::from_text(text),
            Err(UlidError::NonCanonicalCharacter { index, byte }),
            "refused the non-canonical byte in {text:?}",
        );
    }

    // Twenty-six canonical characters that still overflow 128 bits: the
    // first character carries the top two bits only.
    for text in ["8", "9", "A", "Z"] {
        let overflow = format!("{text}1ARZ3NDEKTSV4RRFFQ69G5FAV");
        assert_eq!(
            Ulid::from_text(&overflow),
            Err(UlidError::TextOverflow { first: text.as_bytes()[0] }),
            "refused the overflowing first character {text:?}",
        );
    }
    assert_eq!(
        Ulid::from_text("ZZZZZZZZZZZZZZZZZZZZZZZZZZ"),
        Err(UlidError::TextOverflow { first: b'Z' }),
    );

    // The divergence this wrapper exists to close: the pinned crate's own
    // parser accepts lowercase and wraps overflowing text, so the engine must
    // never hand caller-supplied text to it directly.
    assert!(::ulid::Ulid::from_string("01arz3ndektsv4rrffq69g5fav").is_ok());
    assert!(::ulid::Ulid::from_string("8ZZZZZZZZZZZZZZZZZZZZZZZZZ").is_ok());
}

#[test]
fn wrong_length_binary_is_refused() {
    for bytes in
        [Vec::new(), vec![0; ULID_BINARY_BYTES - 1], vec![0; ULID_BINARY_BYTES + 1], vec![1; 32]]
    {
        assert_eq!(
            Ulid::from_binary(&bytes),
            Err(UlidError::WrongBinaryLength(bytes.len())),
            "refused a {}-byte binary value",
            bytes.len(),
        );
    }
    assert_eq!(
        Ulid::from_binary(&SPEC_EXAMPLE_BYTES),
        Ok(Ulid::from_text(SPEC_EXAMPLE).expect("canonical text parses")),
    );
}

#[test]
fn text_and_binary_round_trip_across_the_corpus() {
    let mut values = vec![
        Ulid::min(),
        Ulid::max(),
        Ulid::from_parts(0, MAX_ENTROPY).expect("the entropy maximum is representable"),
    ];
    values.extend([0, 1, 7, 8, 64, 79, 80, 127].map(|shift| {
        Ulid::from_parts(1_u64 << (shift % 48), 1_u128 << (shift % 80))
            .expect("the components are in range")
    }));
    values.extend((0..16_u32).map(|index| {
        Ulid::from_bytes([u8::try_from(index * 17 % 256).expect("below 256"); ULID_BINARY_BYTES])
    }));

    for value in values {
        let text = value.to_text();
        assert_eq!(text.len(), ULID_TEXT_LENGTH);
        assert!(text.bytes().all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit()));
        let from_text = Ulid::from_text(&text).expect("canonical text parses");
        let from_binary = Ulid::from_binary(&from_text.to_bytes()).expect("sixteen bytes parse");
        assert_eq!(from_binary, value);
        assert_eq!(from_binary.to_text(), text);
        // The buffered writer produces exactly the same form.
        let mut buffer = [0; ULID_TEXT_LENGTH];
        assert_eq!(&*value.write_text(&mut buffer), text);
        // The typed wire constructor and the conversion traits agree.
        assert_eq!(Ulid::from_bytes(value.to_bytes()), value);
        assert_eq!(Ulid::try_from(value.to_bytes().as_slice()), Ok(value));
    }
}

#[test]
fn byte_order_is_chronological_order_and_matches_storage_key_comparison() {
    let earlier = Ulid::from_parts(FIXED_MILLIS, 1).expect("components in range");
    let later_same_millisecond = Ulid::from_parts(FIXED_MILLIS, 2).expect("components in range");
    let later_millisecond = Ulid::from_parts(FIXED_MILLIS + 1, 0).expect("components in range");
    let specification_example =
        Ulid::from_text(SPEC_EXAMPLE).expect("the canonical example parses");

    let ordered = [
        Ulid::min(),
        specification_example,
        earlier,
        later_same_millisecond,
        later_millisecond,
        Ulid::max(),
    ];
    for window in ordered.windows(2) {
        let (lower, higher) = (window[0], window[1]);
        assert!(lower < higher);
        assert!(lower.to_bytes() < higher.to_bytes());
        assert_eq!(
            compare_storage_keys(&lower.to_bytes(), &higher.to_bytes()),
            std::cmp::Ordering::Less,
        );
        assert_eq!(
            compare_storage_keys(&higher.to_bytes(), &lower.to_bytes()),
            std::cmp::Ordering::Greater,
        );
    }
    assert_eq!(
        compare_storage_keys(&Ulid::max().to_bytes(), &Ulid::max().to_bytes()),
        std::cmp::Ordering::Equal,
    );
}

#[test]
fn same_millisecond_generation_is_strictly_increasing() {
    let generator = MonotonicGenerator::with_clock(frozen_clock());
    let mut previous: Option<Ulid> = None;
    let mut seen = HashSet::new();
    for _ in 0..5000 {
        let value = generator.generate();
        assert_eq!(value.timestamp_millis(), FIXED_MILLIS);
        if let Some(previous) = previous {
            assert!(value > previous, "the sequence is strictly increasing");
            assert_eq!(value.timestamp_millis(), previous.timestamp_millis());
        }
        assert!(seen.insert(value.to_bytes()), "no generated value repeats");
        previous = Some(value);
    }
}

#[test]
fn entropy_overflow_waits_for_a_later_millisecond_instead_of_wrapping() {
    // A clock that holds FIXED_MILLIS for the first three reads and then
    // steps forward, so the documented wait provably terminates.
    let polls = Arc::new(AtomicU64::new(0));
    let counted = polls.clone();
    let clock: Clock = Arc::new(move || {
        let poll = counted.fetch_add(1, Ordering::Relaxed);
        if poll < 3 { FIXED_MILLIS } else { FIXED_MILLIS + 1 }
    });
    let generator = MonotonicGenerator::with_clock(clock);
    let exhausted = Ulid::from_parts(FIXED_MILLIS, MAX_ENTROPY)
        .expect("the maximal entropy component is representable");
    generator.set_previous(exhausted);

    let next = generator.generate();
    // The wait happened: the clock was polled more than once before the
    // value appeared, and the value lives in a strictly later millisecond
    // rather than wrapping entropy inside the exhausted one.
    assert!(polls.load(Ordering::Relaxed) >= 3, "generation waited out the overflow");
    assert!(next.timestamp_millis() > exhausted.timestamp_millis());
    assert!(next > exhausted);
    assert!(next.entropy() <= MAX_ENTROPY);

    // And the sequence continues monotonically inside the new millisecond.
    let following = generator.generate();
    assert!(following > next);
}

#[test]
fn a_backwards_clock_never_breaks_monotonicity() {
    // The clock steps backwards after the first draw; the generator must keep
    // emitting inside the last millisecond it used, never a smaller value.
    let generator = MonotonicGenerator::with_clock(Arc::new(|| FIXED_MILLIS));
    let first = generator.generate();
    assert_eq!(first.timestamp_millis(), FIXED_MILLIS);

    let stepped_back = MonotonicGenerator::with_clock(Arc::new(|| FIXED_MILLIS - 7));
    stepped_back.set_previous(first);
    let second = stepped_back.generate();
    assert!(second > first);
    assert_eq!(second.timestamp_millis(), FIXED_MILLIS);
}

#[test]
fn an_overflowing_clock_is_clamped_to_the_ulid_timestamp_range() {
    let generator = MonotonicGenerator::with_clock(Arc::new(|| u64::MAX));
    let value = generator.generate();
    assert_eq!(value.timestamp_millis(), MAX_TIMESTAMP_MILLIS);
}

#[test]
fn concurrent_generation_is_unique_and_per_thread_increasing() {
    let generator = Arc::new(MonotonicGenerator::new());
    let mut all_values = Vec::with_capacity(CONCURRENT_THREADS * CONCURRENT_VALUES_PER_THREAD);
    let mut joins = Vec::with_capacity(CONCURRENT_THREADS);
    for _ in 0..CONCURRENT_THREADS {
        let generator = Arc::clone(&generator);
        joins.push(std::thread::spawn(move || {
            let mut thread_values = Vec::with_capacity(CONCURRENT_VALUES_PER_THREAD);
            for _ in 0..CONCURRENT_VALUES_PER_THREAD {
                thread_values.push(generator.generate().to_bytes());
            }
            thread_values
        }));
    }
    for join in joins {
        all_values.extend(join.join().expect("the generation thread finished"));
    }

    assert_eq!(all_values.len(), CONCURRENT_THREADS * CONCURRENT_VALUES_PER_THREAD);
    let distinct: HashSet<_> = all_values.iter().copied().collect();
    assert_eq!(distinct.len(), all_values.len(), "every concurrently generated value is distinct");

    // Each thread observed its own subsequence strictly increasing, which is
    // the property callers rely on for per-stream ordering.
    let per_thread = all_values
        .chunks(CONCURRENT_VALUES_PER_THREAD)
        .map(|chunk| chunk.iter().map(|bytes| Ulid::from_bytes(*bytes)).collect::<Vec<_>>());
    for thread_values in per_thread {
        for window in thread_values.windows(2) {
            assert!(window[0] < window[1], "each thread's values are strictly increasing");
        }
    }
}

#[test]
fn placement_digest_and_bucket_are_stable_and_in_range() {
    const BUCKET_SPACE: u32 = 1 << 20;
    let example = Ulid::from_text(SPEC_EXAMPLE).expect("the canonical example parses");
    let identities = [example.to_bytes(), [0; ULID_BINARY_BYTES], [0xFF; ULID_BINARY_BYTES]];

    // Golden digests and buckets: the placement identity is a contract, so
    // the corpus anchors it exactly as the XXH64 reference vectors. Changing
    // these bytes re-places every entity, which is a placement-format
    // decision, not an implementation detail.
    let pinned = [
        ("3b00de85b434eb2680e97ad8b59ead977a1ba1a922aa2a3c213a384786bf5a97", 241_677),
        ("1a63dba61d8da9bec6ce195d31ec7ad199f227c55fc89053ff1a7a9ab90c3f89", 108_093),
        ("e011c35eab89d92619af7a1144326404f47fc9d786413f3fbf2e84e11517fe64", 917_788),
    ];
    for (identity, (digest, bucket)) in identities.iter().zip(pinned) {
        assert_eq!(hex(&placement_digest(identity)), digest, "the placement digest is pinned");
        assert_eq!(
            placement_bucket(identity, BUCKET_SPACE),
            Ok(bucket),
            "the placement bucket is pinned",
        );
        // Determinism: the same identity always places identically.
        assert_eq!(placement_digest(identity), placement_digest(identity));
        assert!(placement_bucket(identity, BUCKET_SPACE).expect("non-empty") < BUCKET_SPACE);
    }

    // Distinct identities place independently: no shared digest.
    let digests: Vec<_> = identities.iter().map(placement_digest).collect();
    assert_ne!(digests[0], digests[1]);
    assert_ne!(digests[0], digests[2]);
    assert_ne!(digests[1], digests[2]);

    // Hashed placement spreads identities instead of clustering them on the
    // timestamp prefix, which is the ADR's reason for hashing at all.
    let bucket_space = BUCKET_SPACE;
    let mut occupied = HashSet::new();
    let generator = MonotonicGenerator::new();
    for _ in 0..1024 {
        let identity = generator.generate().to_bytes();
        let bucket =
            placement_bucket(&identity, bucket_space).expect("the bucket space is non-empty");
        assert!(bucket < bucket_space);
        occupied.insert(bucket);
    }
    assert!(occupied.len() > 16, "placement spreads rather than clustering on a prefix");

    // A one-bucket space places everything in bucket zero, and a zero-bucket
    // space is a typed refusal rather than a sentinel bucket.
    assert_eq!(placement_bucket(&identities[0], 1), Ok(0));
    assert_eq!(placement_bucket(&identities[0], 0), Err(IdentityPlacementError::ZeroBucketCount),);
}

/// Encodes bytes as lowercase hex, the form the golden digest rows use.
fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(text, "{byte:02x}");
    }
    text
}

#[test]
fn the_region_boundary_row_accepts_only_registered_uppercase_codes() {
    let catalog = RegionCatalog::production();
    for text in ["GRU", "SFO", "FRA"] {
        let code = catalog.parse(text).expect("a registered uppercase code parses");
        assert_eq!(code.as_str(), text);
    }
    for text in ["gru", "sfo", "fra"] {
        assert_eq!(
            catalog.parse(text),
            Err(RegionCodeError::NotUppercaseLetter { index: 0, byte: text.as_bytes()[0] }),
            "lowercase Region text is never normalized",
        );
    }
    for text in ["SJC", "ABC", "XYZ"] {
        let refusal = catalog.parse(text).expect_err("an unregistered code is refused");
        assert!(matches!(refusal, RegionCodeError::Unregistered(_)));
    }
    for text in ["us-east-1", "IAD", "gru ", ""] {
        assert!(catalog.parse(text).is_err(), "provider and malformed Region text is refused");
    }

    // The identity surface itself: the ADR's entity list is exactly the
    // twenty-seven typed identities above, and none of them is a Region —
    // a Region has no ULID.
    assert_eq!(ENTITY_IDENTITY_NAMES.len(), 27);
    assert!(
        ENTITY_IDENTITY_NAMES.iter().all(|name| !name.contains("Region")),
        "no entity identity type is a Region identity",
    );
    assert_eq!(std::any::type_name::<super::ClusterUlid>(), ENTITY_IDENTITY_NAMES[0]);
    assert_eq!(
        std::any::type_name::<super::CausationUlid>(),
        *ENTITY_IDENTITY_NAMES.last().expect("the list is non-empty")
    );
    let listed: HashSet<_> = ENTITY_IDENTITY_NAMES.iter().copied().collect();
    assert_eq!(
        listed.len(),
        ENTITY_IDENTITY_NAMES.len(),
        "every entity identity type is listed once"
    );
}

#[test]
fn typed_entity_identities_carry_one_form_each_and_never_conflate() {
    let example = Ulid::from_text(SPEC_EXAMPLE).expect("the canonical example parses");
    let commit = super::CommitUlid::from_value(example);
    let document = super::DocumentUlid::from_value(example);

    // One text form and one binary form per identity, whatever the entity.
    assert_eq!(commit.to_text(), SPEC_EXAMPLE);
    assert_eq!(document.as_bytes(), SPEC_EXAMPLE_BYTES);
    assert_eq!(commit.timestamp_millis(), SPEC_EXAMPLE_MILLIS);
    assert_eq!(super::CommitUlid::from_text(&commit.to_text()), Ok(commit),);
    assert_eq!(super::DocumentUlid::from_binary(&document.as_bytes()), Ok(document),);
    assert_eq!(super::DocumentUlid::from_wire_bytes(document.as_bytes()), document,);
    let parsed: super::CommitUlid = SPEC_EXAMPLE.parse().expect("FromStr uses the strict parser");
    assert_eq!(parsed, commit);

    // The types are distinct: no entity identity of one kind is accepted
    // where another is expected, and their identities as types differ.
    assert_ne!(
        std::any::type_name::<super::CommitUlid>(),
        std::any::type_name::<super::DocumentUlid>()
    );

    // The strict refusals surface unchanged through every typed parser.
    assert_eq!(
        super::ClusterUlid::from_text("01arz3ndektsv4rrffq69g5fav"),
        Err(UlidError::NonCanonicalCharacter { index: 2, byte: b'a' }),
    );
    assert_eq!(super::ReplicaUlid::from_binary(&[0; 15]), Err(UlidError::WrongBinaryLength(15)),);

    // Minted identities are canonical and mutually distinct values.
    let first = super::ClientRequestUlid::generate();
    let second = super::ClientRequestUlid::generate();
    assert!(second.value() > first.value(), "minting is monotonic inside one process");
    assert_eq!(first.to_text().len(), ULID_TEXT_LENGTH);
    assert_ne!(first.to_text(), second.to_text());
    let correlation = super::CorrelationUlid::generate();
    assert_ne!(
        std::any::type_name::<super::CorrelationUlid>(),
        std::any::type_name::<super::ClientRequestUlid>()
    );
    assert_ne!(correlation.value(), first.value());
}

#[test]
fn the_process_generator_is_one_shared_monotonic_authority() {
    let first = generate::process_generator().generate();
    let second = super::process_generator().generate();
    let typed = super::SnapshotUlid::generate();
    assert!(second > first, "the process generator keeps one strictly increasing sequence");
    assert!(typed.value() > second, "typed constructors draw from the same authority");
}
